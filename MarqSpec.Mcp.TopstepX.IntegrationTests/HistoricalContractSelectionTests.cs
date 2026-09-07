using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Which contract a range is fetched from — the present band from the venue's own pick, history from the
/// contract that carried the volume (ADR-0020, gh#505).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the unit tier because the answer is read off <b>stored provenance</b>: where the
/// present band starts is the first bucket of the store's trailing run of the venue front, and that is a
/// query over rows a raw-SQL upsert wrote (gh#387).
/// </para>
/// <para>
/// Every instant is a <b>literal</b>, never <c>now - BarCacheService.PresentHorizon</c>. Derived from the
/// constant, an expectation moves with it, and the test that exists to catch a change to the horizon would
/// follow it and stay green — the reason <c>BarCacheServiceTests.SettledNow</c> gives for the same rule.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class HistoricalContractSelectionTests : IAsyncLifetime
{
    /// <summary>The contract the venue marks active.</summary>
    private const string Front = "CON.F.US.MES.U26";

    /// <summary>The contract that was front a roll ago.</summary>
    private const string Previous = "CON.F.US.MES.H26";

    private static readonly InstrumentId _mes = new("MES");

    private readonly SeriesStoreFixture _fixture;

    private readonly TopstepXDbContext _database;
    private readonly HostTelemetry _telemetry = new();

    /// <param name="fixture">The shared container.</param>
    public HistoricalContractSelectionTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
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

    /// <summary>A Tuesday mid-session, so every bucket seeded below is one the venue owed us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    [Fact]
    public async Task TheTenureStart_IsTheTrailingRunOfTheFront_NotItsEarliestBucket()
    {
        // A store that has lived through a roll holds the front's id in more than one place: the run it is
        // laying down now, and older runs a backfill stamped with it before the previous contract's bars were
        // healed back in (ADR-0011's interleaving). Anchoring the present band on the front's EARLIEST bucket
        // would hand the whole interleaved stretch to the venue's pick and never ask the volume winner about
        // it -- which is the defect, wearing the fix's clothes.
        //
        // The seeded shape, five-minute buckets from the session start:
        //   H26  +0 .. +15     the previous front
        //   U26  +20 .. +35    an early, INTERRUPTED run of the front
        //   H26  +40 .. +55    the interleaved backfill
        //   U26  +60 .. +95    the trailing run
        // and, at FIFTEEN minutes, one more H26 bucket at +75 -- inside the five-minute trailing run.
        //
        // That last row is the point of "across all stored resolutions". The front's tenure is uninterrupted
        // only from +80; a query scoped to the resolution being read would answer +60 and hand four buckets
        // the previous contract still holds provenance for to the wrong contract.
        await SeedAsync(Previous, 5, 0, 4);
        await SeedAsync(Front, 5, 20, 4);
        await SeedAsync(Previous, 5, 40, 4);
        await SeedAsync(Front, 5, 60, 8);
        await SeedAsync(Previous, 15, 75, 1);

        BarCacheService cache = Build(SessionStart.AddHours(4));

        DateTimeOffset tenureStart = await cache.TenureStartAsync(
            "test", _mes, Front, SessionStart.AddHours(4), CancellationToken.None);

        tenureStart.Should().Be(SessionStart.AddMinutes(80));
    }

    [Fact]
    public async Task AColdStore_UsesThePresentHorizon()
    {
        // Nothing stored is not evidence that the venue's pick traded forever. Answering a cold store with
        // the beginning of time would make every first read a present-band read on one contract -- the
        // thin-series defect, arrived at from the other direction -- and answering it with `now` would send
        // a week of "latest bars" down the historical path for no gain. Seven days, and no store touched.
        //
        // Both instants are LITERALS. Written as `now - BarCacheService.PresentHorizon` the expectation
        // would move with the constant, and this test -- which exists to catch exactly that change -- would
        // follow it and stay green.
        DateTimeOffset now =
            MarketClock.FromMarket(new DateOnly(2026, 8, 21), new TimeOnly(9, 0)).ToUniversalTime();
        DateTimeOffset sevenDaysBack =
            MarketClock.FromMarket(new DateOnly(2026, 8, 14), new TimeOnly(9, 0)).ToUniversalTime();

        BarCacheService cache = Build(now);

        DateTimeOffset tenureStart = await cache.TenureStartAsync(
            "test", _mes, Front, now, CancellationToken.None);

        tenureStart.Should().Be(sevenDaysBack);
    }

    [Fact]
    public void EveryHandBuiltCache_CarriesARegistryServingItsOwnInstrument()
    {
        // A registry is only useful for the instruments it was configured with: CycleFor and
        // CandidateDepthFor throw KeyNotFoundException for anything else. So a fixture that hands the cache a
        // registry built from DIFFERENT options than the instrument it then reads is a KeyNotFoundException
        // waiting for the first historical fetch -- and it would arrive as a thrown tool call, not as a red
        // test, because nothing in the fetch flow consults the registry yet.
        //
        // ConcurrencyHarness is the one fixture where the two disagree. It serves TWO symbols: ES for
        // everything, and MNQ for the rebuild test, which needs its own instrument because rebuild-indicators
        // filters by instrument rather than by venue and would otherwise reconcile every other test's series.
        // Registry() already names both; the cache has to be given THAT one, not a default.
        BarCacheService cache = ConcurrencyHarness.Cache(
            _database, "test", [], SessionStart.AddHours(2));

        InstrumentRegistry registry = RegistryOf(cache);

        registry.CycleFor(ConcurrencyHarness.RebuildInstrument).Code.Should().Be("HMUZ");
        registry.CandidateDepthFor(ConcurrencyHarness.RebuildInstrument).Should().Be(2);

        registry.CycleFor(ConcurrencyHarness.Instrument).Code.Should().Be("HMUZ");
        registry.CandidateDepthFor(ConcurrencyHarness.Instrument).Should().Be(2);
    }

    /// <summary>The registry a service was constructed with.</summary>
    /// <param name="cache">The service.</param>
    /// <returns>The registry.</returns>
    /// <remarks>
    /// Read off the field because the service exposes no registry, and it should not: nothing outside the
    /// fetch flow has a reason to ask it for one. The alternative — a behavioural assertion — cannot be
    /// written until the fetch actually consults the registry, and the mistake this catches is one that would
    /// then throw rather than answer wrongly. Reflection is the cheap proof available today.
    /// </remarks>
    private static InstrumentRegistry RegistryOf(BarCacheService cache) =>
        (InstrumentRegistry)typeof(BarCacheService)
            .GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    /// <summary>Seeds a run of buckets under one contract id.</summary>
    /// <param name="contractId">The contract the rows are attributed to.</param>
    /// <param name="resolutionMinutes">The resolution to seed under.</param>
    /// <param name="fromMinute">Minutes past <see cref="SessionStart"/> the run opens at.</param>
    /// <param name="count">How many buckets the run holds.</param>
    /// <remarks>
    /// <b>The tracker is cleared afterwards (gh#387).</b> These rows go in through the change tracker while
    /// the reads under test are ordinary queries; left tracked, the identity map would answer them with the
    /// instances this method wrote rather than with the rows the store holds.
    /// </remarks>
    private async Task SeedAsync(string contractId, int resolutionMinutes, int fromMinute, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = _mes.Symbol,
                ResolutionMinutes = resolutionMinutes,
                BucketStart = SessionStart.AddMinutes(fromMinute + (i * resolutionMinutes)),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100.5m,
                Volume = 1_000,
                ContractId = contractId,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>Builds the cache over a venue listing both the front and the contract behind it.</summary>
    /// <param name="now">The instant to read at.</param>
    /// <returns>The service.</returns>
    private BarCacheService Build(DateTimeOffset now)
    {
        CountingGateway gateway = new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [Front] = [],
                [Previous] = [],
            },
            Front);

        FakeTimeProvider clock = new(now);
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);

        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);

        IndicatorProjector projector =
            new(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);

        IOptions<MarketDataOptions> market = Options.Create(new MarketDataOptions
        {
            Instruments = "MES",
            SessionCloseCentral = "16:00",
            MaxRows = 5_000,
        });

        return new BarCacheService(
            _database,
            gateway,
            calendar,
            projector,
            new InstrumentRegistry(market),
            new ContractDirectory(clock),
            clock,
            NullLogger<BarCacheService>.Instance,
            _telemetry);
    }
}
