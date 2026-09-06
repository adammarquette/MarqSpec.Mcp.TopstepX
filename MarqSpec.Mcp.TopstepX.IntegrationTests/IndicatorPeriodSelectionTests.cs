using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// An indicator configured at two periods is stored at both, and the snapshot still reports the PRIMARY one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The map is keyed by NAME and the store is keyed by <c>(name, period)</c>, so widening the catalogue
/// puts two rows in reach of one key.</b> <c>GetLatestIndicatorReadings</c> matches its rows back against the
/// catalogue and writes <c>readings[indicator.Name]</c>; walking every configured instance therefore lets the
/// last one of a name win. With <c>Indicators__AdditionalEmaPeriods=3</c> set, <c>get_market_snapshot</c>
/// would have published the three-bar EMA under <c>ema</c> while <c>get_indicators</c> and
/// <c>get_indicator_at</c> answered with the five-bar one — the same key, two windows, and nothing in the
/// payload saying which. That is a plausible number that is acted on, the failure mode ADR-0011 and gh#286
/// are both about, so it is measured here rather than reasoned about.
/// </para>
/// <para>
/// <b>This tier, because both claims are about stored rows.</b> The periods have to be projected before
/// either can be read, and a projection is a write — the real <c>UpsertValuesSql</c> under the
/// <c>RepeatableRead</c> transaction the server opens, which only Postgres executes (gh#387).
/// </para>
/// <para>
/// The composition below is <see cref="SnapshotIndicatorProvenanceTests"/>'s, shortened: one contract rather
/// than a roll, because provenance across a seam is already pinned there and what is under test here is
/// which period a name resolves to.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorPeriodSelectionTests : IAsyncLifetime
{
    private const int Resolution = 5;
    private const int SeededBars = 40;
    private const string Contract = "CON.F.US.TEST.Z26";

    /// <summary>The EMA period a caller gets by omitting <c>period</c>.</summary>
    private const int PrimaryEma = 5;

    /// <summary>The additional EMA period, and the one the map must NOT report.</summary>
    /// <remarks>
    /// Three, so the smoothing factor is <c>2 / (3 + 1)</c> — exactly <c>0.5</c> in <c>decimal</c>. Period 2
    /// would give <c>2 / 3</c>, which is not, and a rounding artefact is the last thing an equality
    /// assertion about the wrong period should be able to turn on.
    /// </remarks>
    private const int AdditionalEma = 3;

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public IndicatorPeriodSelectionTests(SeriesStoreFixture fixture)
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
        return Task.CompletedTask;
    }

    /// <summary>A Tuesday mid-session, so every bucket is one the venue owed us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(Resolution * index);

    [Fact]
    public async Task TheSnapshotMap_ReportsThePrimaryPeriod_WhenAnAdditionalPeriodIsAlsoStored()
    {
        (SnapshotTools snapshot, IndicatorTools indicators) = await ComposeAsync();

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [Resolution], SeededBars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        // The anchor the slice read at, reconstructed the way the tool does it: the last bar's bucket.
        DateTimeOffset asOf = slice.Bars[^1].T;

        // What the caller gets from the single-purpose read with the period OMITTED -- which is the primary,
        // and is what the map has always meant by "ema".
        ToolPayloads.IndicatorReading primary =
            await indicators.GetIndicatorAt("ES", Resolution, "ema", asOf, CancellationToken.None);

        primary.Value.Should().NotBeNull(
            "a forty-bar series satisfies a five-bar EMA, or this test is comparing two absences");

        decimal? stored = await StoredValueAsync("ema", AdditionalEma, asOf);

        stored.Should().NotBeNull(
            "the additional period must actually be in the store, or the collision this test exists for "
            + "cannot happen and the assertion below passes by there being nothing to overwrite with");
        stored.Should().NotBe(
            primary.Value,
            "the two windows must disagree over this fixture, or a map reporting the wrong one would be "
            + "indistinguishable from a map reporting the right one");

        slice.Indicators.Should().ContainKey("ema");

        ToolPayloads.IndicatorReading? composed = slice.Indicators["ema"];

        composed.Should().NotBeNull();
        composed!.Value.Should().Be(
            primary.Value,
            "the snapshot's `ema` is the PRIMARY period. The map is keyed by name, so a batched read that "
            + "walked every configured instance would let the last one of a name win -- publishing EMA({0}) "
            + "under a key every caller reads as EMA({1}), with nothing in the payload saying so",
            AdditionalEma,
            PrimaryEma);
        composed.Value.Should().NotBe(stored, "and specifically not the additional period's number");
        composed.BucketStart.Should().Be(primary.BucketStart);
        composed.ContractId.Should().Be(primary.ContractId);

        // The vocabulary did not widen either: twelve names, whatever the periods are configured to.
        slice.Indicators.Should().HaveCount(
            12, "additional periods add instances, never keys -- the map's key set is the catalogue's names");
    }

    [Fact]
    public async Task StoredPairs_HoldEveryConfiguredPeriod_AfterOneReplay()
    {
        // The other half, and without it the test above could pass by the additional period never being
        // computed at all. A period an operator configured and the projection silently skipped reads back as
        // an empty series, which is indistinguishable from a market that produced none.
        await ComposeAsync();

        IReadOnlyList<(string Indicator, int Period)> pairs = await StoredPairsAsync();

        pairs.Should().Contain(("ema", PrimaryEma)).And.Contain(("ema", AdditionalEma),
            "one replay writes every (name, period) the catalogue owns, not one row per name");

        pairs.Where(p => p.Indicator == "ema").Should().HaveCount(
            2, "exactly the two configured windows, and no third one nobody asked for");

        // And the widening is EMA's alone -- the other families were left at one period each, so a projection
        // that fanned every indicator out would show up here rather than passing on the two rows it was
        // asked about.
        pairs.Where(p => p.Indicator == "atr").Should().ContainSingle();
        pairs.Should().OnlyHaveUniqueItems();
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bars over a half-open index range, drifting irregularly under one contract.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// A tidy ramp is the one series on which a three-bar and a five-bar EMA converge, and this test turns on
    /// their disagreeing — so the closes drift by a repeating but uneven amount instead.
    /// </remarks>
    private static IReadOnlyList<Bar> Bars(int fromIndex, int toIndexExclusive) =>
    [
        .. Enumerable.Range(fromIndex, toIndexExclusive - fromIndex).Select(i =>
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);
            return new Bar(Bucket(i), close, close + 1.25m, close - 0.75m, close, 1_000 + i, Contract);
        }),
    ];

    /// <summary>The latest stored value of one <c>(indicator, period)</c> at or before a moment.</summary>
    /// <param name="indicator">The indicator name.</param>
    /// <param name="period">The stored period.</param>
    /// <param name="asOf">The moment.</param>
    /// <returns>The value, or <see langword="null"/> when the store holds no such row.</returns>
    /// <remarks>
    /// Read straight from the store rather than through <c>get_indicator_at</c>, because the tool's period
    /// argument does not exist yet — selecting among the configured periods is the next task. What this test
    /// needs is only the number the additional window produced, so that the number the map publishes can be
    /// shown to be the other one.
    /// </remarks>
    private async Task<decimal?> StoredValueAsync(string indicator, int period, DateTimeOffset asOf) =>
        await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == "test"
                && v.Instrument == "ES"
                && v.ResolutionMinutes == Resolution
                && v.Indicator == indicator
                && v.Period == period
                && v.BucketStart <= asOf)
            .OrderByDescending(v => v.BucketStart)
            .Select(v => (decimal?)v.Value)
            .FirstOrDefaultAsync();

    /// <summary>The distinct <c>(Indicator, Period)</c> pairs the store holds for the seeded series.</summary>
    /// <returns>The pairs, ordered by indicator name and then by period.</returns>
    /// <remarks>
    /// The same shape <see cref="IndicatorReadProjectionTests"/> uses, and the order is stated in the query
    /// rather than imposed on the materialised list — a <c>SELECT DISTINCT</c> with no <c>ORDER BY</c> comes
    /// back in whatever order a HashAggregate plan produces (gh#432). The anonymous projection is
    /// deliberate: a <c>ValueTuple</c> inside a LINQ <c>Select</c> translates to a Postgres <c>record</c> and
    /// fails only against a real one, so the tuple is built after materialisation.
    /// </remarks>
    private async Task<IReadOnlyList<(string Indicator, int Period)>> StoredPairsAsync()
    {
        var held = await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == "test"
                && v.Instrument == "ES"
                && v.ResolutionMinutes == Resolution)
            .Select(v => new { v.Indicator, v.Period })
            .Distinct()
            .OrderBy(v => v.Indicator)
            .ThenBy(v => v.Period)
            .ToListAsync();

        return [.. held.Select(v => (v.Indicator, v.Period))];
    }

    /// <summary>
    /// Seeds forty bars under one contract, projects the catalogue over them, and composes the snapshot.
    /// </summary>
    /// <returns>The snapshot tool and the indicator tool it composes.</returns>
    /// <remarks>
    /// <b>One wiring, both tools.</b> The claim is that the batched map and the single as-of read agree about
    /// which period <c>ema</c> means, and two independently wired tools could agree by having been handed the
    /// same fixture twice while disagreeing about the same one.
    /// </remarks>
    private async Task<(SnapshotTools Snapshot, IndicatorTools Indicators)> ComposeAsync()
    {
        foreach (Bar bar in Bars(0, SeededBars))
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = Resolution,
                BucketStart = bar.OpenTime,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                ContractId = bar.ContractId,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();

        IOptions<MarketDataOptions> wrapped = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);

        // EMA at two windows and nothing else widened, so any second period that shows up in the store came
        // from the catalogue fanning out where it was not asked to.
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions
            {
                EmaPeriod = PrimaryEma,
                AdditionalEmaPeriods = "3",
            }),
            calendar);

        FakeTimeProvider clock = new(Bucket(SeededBars).AddHours(2));

        // Serves nothing: the window each case reads is filled from the store alone.
        CountingGateway gateway = new([]);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);

        // WRAPPED IN THE TRANSACTION PRODUCTION USES (gh#387). The projector refuses to run outside one, and
        // RepeatableRead is restated by hand because SeriesUnitOfWork, which states it once for production,
        // is internal.
        await using (IDbContextTransaction seed = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None))
        {
            await projector.ProjectAsync(
                "test", new InstrumentId("ES"), Resolution, SessionStart, CancellationToken.None);
            await _database.SaveChangesAsync();
            await seed.CommitAsync(CancellationToken.None);
        }

        InstrumentResolver resolver = new(new InstrumentRegistry(wrapped), new StoreAvailabilityHolder());
        ToolGuards guards = new(wrapped);

        IndicatorTools indicators = new(
            resolver,
            _database,
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            gateway,
            guards);

        SnapshotTools snapshot = new(
            new BarTools(
                resolver,
                new BarCacheService(
                    _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance),
                guards,
                clock),
            indicators,
            new KeyLevelTools(
                resolver,
                _database,
                catalog,
                new LevelMethodCatalog(calendar),
                gateway,
                guards,
                new VolumeProfileService(_database),
                Options.Create(new KeyLevelDetectionOptions())),
            new ReferenceTools(new InstrumentRegistry(wrapped), calendar, gateway, wrapped, clock),
            new IndicatorCatalogNames(catalog),
            clock);

        return (snapshot, indicators);
    }
}
