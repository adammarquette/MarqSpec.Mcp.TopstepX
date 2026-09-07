using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The half of the app-owned instruments a store is required to reach: <c>outcome="miss"</c> and
/// <c>outcome="partial"</c>, the gap fills underneath them, and the indicator values a projection writes
/// (gh#536).
/// </summary>
/// <remarks>
/// <para>
/// <b>Here rather than in the unit tier because every one of these reaches a write.</b> A miss fetches and
/// then upserts through <see cref="SeriesUnitOfWork"/>, which opens a <c>RepeatableRead</c> transaction and
/// runs two <c>ON CONFLICT … DO UPDATE</c> statements. The in-memory provider has neither, so the whole
/// fetch-and-project path is unrepresentable there — a suite that faked it would be green on the day the
/// instrument was deleted. The warm half, which reaches no transaction, stays in
/// <c>CacheReadTelemetryTests</c>.
/// </para>
/// <para>
/// <b>The venue is a hand-written double, as this tier's contract requires.</b> No credential is read and no
/// vendor host is dialled; <c>CountingGateway</c> answers from a script and counts what it was asked, which
/// is what makes "a hit costs zero vendor calls" checkable at all.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class TelemetryInstrumentTests : IAsyncLifetime
{
    private static readonly InstrumentId _es = new("ES");

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly HostTelemetry _telemetry = new();
    private readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);
    private readonly IndicatorCatalog _catalog;

    /// <param name="fixture">The shared container.</param>
    public TelemetryInstrumentTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
        _catalog = new IndicatorCatalog(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }),
            _calendar);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A Tuesday mid-session, so every bucket in the window is one the venue owes us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    [Fact]
    public async Task AColdWindowIsAMiss_AndTheSecondReadOfItIsAHit()
    {
        // THE ACCEPTANCE CRITERION, as a test. `outcome="miss"` on the first read of a fresh symbol and
        // `outcome="hit"` on the second is the whole cache-aside claim (R-1.1, R-1.3) stated as a number an
        // operator can watch, and until now it existed only as VenueRequests inside a test.
        CountingGateway gateway = new(VenueBars(12));
        BarCacheService cache = Bars(gateway);

        using MetricCollector<long> reads = Collect(HostTelemetry.CacheReadsInstrument);

        BarRange window = new(SessionStart, SessionStart.AddMinutes(5 * 12));

        BarReadResult first = await cache.GetBarsAsync(_es, 5, window, CancellationToken.None);
        first.VenueRequests.Should().BeGreaterThan(0, "a cold window has to be fetched");

        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Miss);
        reads.LastMeasurement.Tags[HostTelemetry.SymbolTag].Should().Be("ES");
        reads.LastMeasurement.Tags[HostTelemetry.ResolutionTag].Should().Be(5);

        // A SECOND CONTEXT, because the seed above went through raw SQL the change tracker never saw and the
        // scope memo is per-service. This is the read an operator's second call makes.
        BarReadResult second = await Bars(gateway).GetBarsAsync(_es, 5, window, CancellationToken.None);

        second.VenueRequests.Should().Be(0, "the store already holds every bucket the calendar expects");
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Hit);

        reads.GetMeasurementSnapshot().Should().HaveCount(2);
    }

    [Fact]
    public async Task AWindowTheStorePartlyHoldsIsAPartial_NotASecondMiss()
    {
        // A live instrument is always a little behind, so this is the ordinary shape rather than an exotic
        // one. Collapsing it into `miss` would make an ordinary running server report a permanent stream of
        // cache misses, and the panel that is meant to say the cache is working would say the opposite.
        CountingGateway gateway = new(VenueBars(24));
        BarRange narrow = new(SessionStart, SessionStart.AddMinutes(5 * 12));
        BarRange wide = new(SessionStart, SessionStart.AddMinutes(5 * 24));

        await Bars(gateway).GetBarsAsync(_es, 5, narrow, CancellationToken.None);

        using MetricCollector<long> reads = Collect(HostTelemetry.CacheReadsInstrument);

        BarReadResult widened = await Bars(gateway).GetBarsAsync(_es, 5, wide, CancellationToken.None);

        widened.VenueRequests.Should().BeGreaterThan(0);
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(CacheOutcome.Partial);
    }

    [Fact]
    public async Task AColdWindowsGapFillIsCountedAsAbsent()
    {
        CountingGateway gateway = new(VenueBars(12));

        using MetricCollector<long> fills = Collect(HostTelemetry.GapFillsInstrument);

        await Bars(gateway).GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(5 * 12)), CancellationToken.None);

        CollectedMeasurement<long> measurement = fills.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Value.Should().BePositive("the ranges the read actually fetched are what is counted");
        measurement.Tags[HostTelemetry.ReasonTag].Should().Be(GapReason.Absent);
        measurement.Tags[HostTelemetry.SymbolTag].Should().Be("ES");
        measurement.Tags[HostTelemetry.ResolutionTag].Should().Be(5);
    }

    [Fact]
    public async Task AWidenedWindowsGapFillIsCountedAsAGapRatherThanAsAColdRead()
    {
        CountingGateway gateway = new(VenueBars(24));

        await Bars(gateway).GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(5 * 12)), CancellationToken.None);

        using MetricCollector<long> fills = Collect(HostTelemetry.GapFillsInstrument);

        await Bars(gateway).GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(5 * 24)), CancellationToken.None);

        fills.LastMeasurement!.Tags[HostTelemetry.ReasonTag].Should().Be(GapReason.Gap);
    }

    [Fact]
    public async Task ABucketCarryingNoContractIsHealedAndCountedAsUnattributed()
    {
        // The gh#402/gh#412 heal: a row written before migration 20260823074908 is present, numerically
        // correct, and carries no contract id -- so the read path re-asks the venue for it. That is a one-off
        // migration cost rather than a market fact, and it is worth telling apart from an ordinary hole
        // precisely because it should stop happening.
        await SeedUnattributedAsync(VenueBars(12));

        CountingGateway gateway = new(VenueBars(12));

        using MetricCollector<long> fills = Collect(HostTelemetry.GapFillsInstrument);

        await Bars(gateway).GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(5 * 12)), CancellationToken.None);

        fills.LastMeasurement!.Tags[HostTelemetry.ReasonTag].Should().Be(GapReason.Unattributed);
    }

    [Fact]
    public async Task AReadThatFetchesNothingRecordsNoGapFill()
    {
        // The other half of the gate. A warm read must not manufacture a zero-valued `reason` series: the tag
        // set would exist forever in every backend scraping it, describing an event that never fired.
        CountingGateway gateway = new(VenueBars(12));
        BarRange window = new(SessionStart, SessionStart.AddMinutes(5 * 12));

        await Bars(gateway).GetBarsAsync(_es, 5, window, CancellationToken.None);

        using MetricCollector<long> fills = Collect(HostTelemetry.GapFillsInstrument);

        await Bars(gateway).GetBarsAsync(_es, 5, window, CancellationToken.None);

        fills.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task AProjectionCountsItsValuesUnderEachIndicatorName()
    {
        CountingGateway gateway = new(VenueBars(30));

        using MetricCollector<long> projections = Collect(HostTelemetry.IndicatorProjectionsInstrument);

        await Bars(gateway).GetBarsAsync(
            _es, 5, new BarRange(SessionStart, SessionStart.AddMinutes(5 * 30)), CancellationToken.None);

        IReadOnlyList<CollectedMeasurement<long>> measured = projections.GetMeasurementSnapshot();
        measured.Should().NotBeEmpty("the fill projects in the same unit of work as the bar write");

        foreach (CollectedMeasurement<long> measurement in measured)
        {
            measurement.Value.Should().BePositive();
            measurement.Tags[HostTelemetry.SymbolTag].Should().Be("ES");
            measurement.Tags[HostTelemetry.ResolutionTag].Should().Be(5);
            measurement.Tags[HostTelemetry.IndicatorTag].As<string>()
                .Should().BeOneOf(_catalog.All.Select(i => i.Name));
        }

        measured.Select(m => m.Tags[HostTelemetry.IndicatorTag]).Should().OnlyHaveUniqueItems(
            "one measurement per indicator per pass, not one per value");
    }

    [Fact]
    public async Task AConfirmingProjectionRecordsNothing()
    {
        // ADR-0006's empty diff, as a number. Recomputing over the same bars yields the same values, so a
        // confirming pass writes nothing — and a spike here on a rebuild would be saying indicators are not
        // reproducible projections after all.
        CountingGateway gateway = new(VenueBars(30));
        BarRange window = new(SessionStart, SessionStart.AddMinutes(5 * 30));

        await Bars(gateway).GetBarsAsync(_es, 5, window, CancellationToken.None);

        using MetricCollector<long> projections = Collect(HostTelemetry.IndicatorProjectionsInstrument);

        await using TopstepXDbContext second = _fixture.CreateContext();
        IndicatorProjector projector = new(
            second, _catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);

        await using IDbContextTransaction transaction = await second.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await projector.ProjectAsync("test", _es, 5, SessionStart.AddDays(1), CancellationToken.None);

        await second.SaveChangesAsync();
        await transaction.CommitAsync();

        changed.Should().Be(0, "recomputing over the same bars yields the same numbers (ADR-0006)");

        projections.GetMeasurementSnapshot().Should().BeEmpty();
    }

    private BarCacheService Bars(CountingGateway gateway)
    {
        TopstepXDbContext context = _fixture.CreateContext();

        return new BarCacheService(
            context,
            gateway,
            _calendar,
            new IndicatorProjector(context, _catalog, NullLogger<IndicatorProjector>.Instance, _telemetry),
            new FakeTimeProvider(SessionStart.AddDays(3)),
            NullLogger<BarCacheService>.Instance,
            _telemetry);
    }

    private MetricCollector<long> Collect(string instrument) =>
        new(_telemetry, HostTelemetry.Name, instrument);

    private static IReadOnlyList<Bar> VenueBars(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new Bar(SessionStart.AddMinutes(5 * i), 100m + i, 101m + i, 99m + i, 100.5m + i, 1_000))];

    /// <summary>Seeds pre-migration rows: the right numbers, and no recorded contract (gh#402).</summary>
    private async Task SeedUnattributedAsync(IEnumerable<Bar> bars)
    {
        await using TopstepXDbContext seeding = _fixture.CreateContext();

        foreach (Bar bar in bars)
        {
            seeding.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = bar.OpenTime,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                ContractId = null,
                RecordedAt = SessionStart,
            });
        }

        await seeding.SaveChangesAsync();
    }
}
