using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// <c>mcp.cache.reads{outcome="hit"}</c> and the <c>cache.*</c> spans, driven through the three cache-aside
/// services themselves (gh#536).
/// </summary>
/// <remarks>
/// <para>
/// <b>The warm half only, and that boundary is gh#387's.</b> A read that fetches or projects goes through
/// <see cref="SeriesUnitOfWork"/>, which opens a transaction the in-memory provider does not have — so
/// <c>miss</c> and <c>partial</c>, and every gap fill and projection with them, are pinned in the integration
/// tier against a real Postgres (<c>TelemetryInstrumentTests</c> there). What stays here is exactly what the
/// unit tier is for: the answers this server gives <i>without</i> reaching a venue or opening a transaction.
/// </para>
/// <para>
/// <b>A hit is the interesting one anyway.</b> It is the claim the whole cache exists to make — a repeated
/// read costs zero vendor calls — and until now it was observable only as <c>VenueRequests == 0</c> inside a
/// test. This is what makes it observable from outside the process.
/// </para>
/// </remarks>
[Collection(HostTelemetryCollection.Name)]
public sealed class CacheReadTelemetryTests : IDisposable
{
    private const string Venue = "test";
    private const string Contract = CountingGateway.DefaultContractId;
    private const int Resolution = 5;

    /// <summary>Mid-session on an ordinary Tuesday, so every seeded bucket is one the calendar expects.</summary>
    private static readonly DateTimeOffset _sessionStart =
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private readonly TopstepXDbContext _database;
    private readonly HostTelemetry _telemetry = new();
    private readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);
    private readonly FakeTimeProvider _clock;
    private readonly CountingGateway _gateway = new([]);
    private readonly IndicatorCatalog _catalog;

    public CacheReadTelemetryTests()
    {
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        _clock = new FakeTimeProvider(_sessionStart.AddHours(4));
        _catalog = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }),
            _calendar);
    }

    [Fact]
    public async Task AWindowTheStoreAlreadyCoversIsCountedAsAHit_AndCostsNoVenueCall()
    {
        // The cache-aside claim, as a NUMBER rather than as an internal counter. Every bucket the calendar
        // expects is stored and attributed, so the detector finds nothing missing and the venue is never
        // reached -- which is exactly what `outcome="hit"` must mean.
        Seed(bars: 12);

        BarCacheService cache = Bars();

        using MetricCollector<long> reads = Collect(HostTelemetry.CacheReadsInstrument);

        BarReadResult result = await cache.GetBarsAsync(
            new InstrumentId("ES"),
            Resolution,
            new BarRange(_sessionStart, _sessionStart.AddMinutes(Resolution * 12)),
            CancellationToken.None);

        result.VenueRequests.Should().Be(0, "a hit is defined by the venue not being reached");
        _gateway.BarRequests.Should().Be(0);

        CollectedMeasurement<long> measurement = reads.LastMeasurement!;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.SeriesTag].Should().Be(CacheSeries.Bars);
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be("ES");
        measurement.Tags[HostTelemetry.ResolutionTag].Should().Be(Resolution);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Hit);
    }

    [Fact]
    public async Task ABarReadOpensACacheSpanUnderTheAmbientToolCall()
    {
        // "so a tool-call trace in Tempo shows where the time went" (gh#536). In the running host the parent
        // is the SDK's tools/call span; here an ambient activity stands in for it, and what is pinned is that
        // the read appears UNDER it rather than as a second root nothing correlates.
        Seed(bars: 12);

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, _telemetry.Activities);

        using ActivitySource parentSource = new("test.parent");
        using ActivityListener parentListener = Listen([], parentSource);
        using Activity? parent = parentSource.StartActivity("tools/call");

        parent.Should().NotBeNull("the stand-in parent has to be sampled, or this asserts nothing");

        await Bars().GetBarsAsync(
            new InstrumentId("ES"),
            Resolution,
            new BarRange(_sessionStart, _sessionStart.AddMinutes(Resolution * 12)),
            CancellationToken.None);

        Activity span = spans.Should().ContainSingle().Subject;
        span.OperationName.Should().Be("cache." + CacheSeries.Bars);
        span.Parent.Should().Be(parent);
    }

    [Fact]
    public async Task AnIndicatorReadOverASeriesTheStoreHasNoBarsForIsAHit()
    {
        // No bars is not a miss. There is nothing to project FROM, so the store already holds every value the
        // bars justify -- which is zero of them -- and `R-2.3` makes that absence a fact rather than a gap.
        // Counting it as a miss would report a permanent stream of misses for every symbol nobody has ever
        // fetched.
        IndicatorCacheService cache = Indicators();

        using MetricCollector<long> reads = Collect(HostTelemetry.CacheReadsInstrument);

        bool replayed = await cache.EnsureProjectedAsync(
            Venue, new InstrumentId("ES"), Resolution, CancellationToken.None);

        replayed.Should().BeFalse();

        CollectedMeasurement<long> measurement = reads.LastMeasurement!;
        measurement.Tags[HostTelemetry.SeriesTag].Should().Be(CacheSeries.Indicators);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Hit);
    }

    [Fact]
    public async Task ASecondIndicatorReadInOneScopeIsCountedAgain_AndStillAsAHit()
    {
        // The scope memo (`_complete`) exists so get_market_snapshot pays ONE probe per series, not one per
        // catalogue name. That is a store optimisation, and it must not make the read vanish from the
        // numbers: a caller that asked twice was served twice, and a rate that fell because a memo was added
        // would read as traffic that stopped.
        IndicatorCacheService cache = Indicators();

        using MetricCollector<long> reads = Collect(HostTelemetry.CacheReadsInstrument);

        await cache.EnsureProjectedAsync(Venue, new InstrumentId("ES"), Resolution, CancellationToken.None);
        await cache.EnsureProjectedAsync(Venue, new InstrumentId("ES"), Resolution, CancellationToken.None);

        reads.GetMeasurementSnapshot().Should().HaveCount(2);
        cache.Probes.Should().Be(1, "the memo is still doing its job");
    }

    [Fact]
    public async Task AFootprintReadOverAnEmptyTapeIsAHit()
    {
        FootprintCacheService cache = Footprint();

        using MetricCollector<long> reads = Collect(HostTelemetry.CacheReadsInstrument);

        bool replayed = await cache.EnsureProjectedAsync(
            Venue, new InstrumentId("ES"), Resolution, CancellationToken.None);

        replayed.Should().BeFalse();

        CollectedMeasurement<long> measurement = reads.LastMeasurement!;
        measurement.Tags[HostTelemetry.SeriesTag].Should().Be(CacheSeries.Footprint);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Hit);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _database.Dispose();
        _telemetry.Dispose();
    }

    private BarCacheService Bars() =>
        new(
            _database,
            _gateway,
            _calendar,
            new IndicatorProjector(_database, _catalog, NullLogger<IndicatorProjector>.Instance, _telemetry),
            _clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);

    private IndicatorCacheService Indicators() =>
        new(
            _database,
            _catalog,
            new IndicatorProjector(_database, _catalog, NullLogger<IndicatorProjector>.Instance, _telemetry),
            _clock,
            NullLogger<IndicatorCacheService>.Instance,
            readTriggeredReplays: null,
            telemetry: _telemetry);

    private FootprintCacheService Footprint() =>
        new(
            _database,
            new FootprintProjector(_database, NullLogger<FootprintProjector>.Instance),
            _clock,
            NullLogger<FootprintCacheService>.Instance,
            _telemetry);

    private MetricCollector<long> Collect(string instrument) =>
        new(_telemetry, HostTelemetry.Name, instrument);

    private void Seed(int bars)
    {
        for (int i = 0; i < bars; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = Venue,
                Instrument = "ES",
                ResolutionMinutes = Resolution,
                BucketStart = _sessionStart.AddMinutes(Resolution * i),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 1_000,
                ContractId = Contract,
                RecordedAt = _sessionStart,
            });
        }

        _database.SaveChanges();
    }

    /// <summary>Subscribes to one <see cref="ActivitySource"/> INSTANCE and records what it starts.</summary>
    /// <remarks>
    /// <b>Reference equality, not a name match.</b> Every cache service falls back to its own
    /// <c>new HostTelemetry()</c> when DI does not supply the singleton, so about a dozen suites that never
    /// mention telemetry emit <c>cache.*</c> spans under the same name — and they run in PARALLEL with this
    /// one. Matching by name let one of theirs land here and made <c>ContainSingle()</c> see two (gh#536).
    /// </remarks>
    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
