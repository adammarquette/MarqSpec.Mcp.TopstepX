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
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

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
    private readonly HostTelemetry _telemetry = new();

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
        _telemetry.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A Tuesday mid-session, so every bucket is one the venue owed us.</summary>
    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(Resolution * index);

    [Fact]
    public async Task TheSnapshotMap_ReportsThePrimaryPeriod_WhenAnAdditionalPeriodIsAlsoStored()
    {
        Composed wiring = await ComposeAsync();

        ToolPayloads.MarketSnapshot payload = await wiring.Snapshot
            .GetMarketSnapshot("ES", [Resolution], SeededBars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        // The anchor the slice read at, reconstructed the way the tool does it: the last bar's bucket.
        DateTimeOffset asOf = slice.Bars[^1].T;

        // What the caller gets from the single-purpose read with the period OMITTED -- which is the primary,
        // and is what the map has always meant by "ema".
        ToolPayloads.IndicatorReading primary = await wiring.Indicators
            .GetIndicatorAt("ES", Resolution, "ema", asOf, cancellationToken: CancellationToken.None);

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

    // ── Selecting a period on the single-series reads ─────────────────────────────────

    [Fact]
    public async Task GetIndicators_ReadsTheAdditionalPeriod_WhenItIsRequested()
    {
        Composed composed = await ComposeAsync();

        ToolPayloads.IndicatorSeries series = await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            AdditionalEma,
            cancellationToken: CancellationToken.None);

        series.Period.Should().Be(
            AdditionalEma,
            "the payload reports the period that RAN, so a caller can tell which window they were served");

        // HAND-CHECKED, not round-tripped. An EMA begins at the simple average of its first window, so the
        // first value of an EMA(3) is (c0 + c1 + c2) / 3 -- and the store keeps eight decimal places.
        decimal[] closes = [.. Bars(0, AdditionalEma).Select(b => b.Close)];

        closes.Should().Equal(
            [5_000m, 4_999.09m, 5_004.26m],
            "the arithmetic below is checked against these three closes; a fixture that drifted would make "
            + "the expected number quietly wrong rather than the assertion fail");

        decimal seed = decimal.Round(
            (closes[0] + closes[1] + closes[2]) / 3m,
            TopstepXDbContext.PriceScale,
            MidpointRounding.AwayFromZero);

        seed.Should().Be(5_001.11666667m, "15003.35 / 3, to the store's eight places");

        series.Values.Should().NotBeEmpty("forty bars satisfy a three-bar window many times over");
        series.Values[0].T.Should().Be(
            Bucket(AdditionalEma - 1), "an EMA(3) measures from the third bar, not the fifth");
        series.Values[0].V.Should().Be(seed);
    }

    [Fact]
    public async Task GetIndicators_ReadsThePrimaryPeriod_WhenPeriodIsOmitted()
    {
        // Omitting the argument must keep meaning exactly what it meant before it existed. If it drifted to
        // "whichever instance the catalogue built last", every caller written against the old surface would
        // silently start reading a different window.
        Composed composed = await ComposeAsync();

        ToolPayloads.IndicatorSeries omitted = await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            cancellationToken: CancellationToken.None);

        omitted.Period.Should().Be(PrimaryEma);
        omitted.Values.Should().NotBeEmpty();
        omitted.Values[0].T.Should().Be(
            Bucket(PrimaryEma - 1), "a five-bar EMA cannot measure until the fifth bar");

        ToolPayloads.IndicatorSeries selected = await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            AdditionalEma,
            cancellationToken: CancellationToken.None);

        omitted.Values[^1].V.Should().NotBe(
            selected.Values[^1].V,
            "the two windows must disagree over this fixture, or serving the wrong one would be "
            + "indistinguishable from serving the right one");
    }

    [Fact]
    public async Task GetIndicatorAt_ReadsTheAdditionalPeriod_WhenItIsRequested()
    {
        // The as-of read is the one get_market_snapshot composes, and it answers cannot-measure when it
        // matches no row -- so a period argument it ignored would look like an honest absence, or worse like
        // an honest number from the other window.
        Composed composed = await ComposeAsync();

        DateTimeOffset asOf = Bucket(SeededBars - 1);

        ToolPayloads.IndicatorReading selected = await composed.Indicators.GetIndicatorAt(
            "ES", Resolution, "ema", asOf, AdditionalEma, cancellationToken: CancellationToken.None);

        decimal? stored = await StoredValueAsync("ema", AdditionalEma, asOf);

        stored.Should().NotBeNull("the additional window is in the store, or this proves nothing");
        selected.Value.Should().Be(stored, "the read must answer from the row the period names");
        selected.BucketStart.Should().Be(asOf);
        selected.ContractId.Should().Be(Contract);

        ToolPayloads.IndicatorReading omitted = await composed.Indicators.GetIndicatorAt(
            "ES", Resolution, "ema", asOf, cancellationToken: CancellationToken.None);

        omitted.Value.Should().NotBe(selected.Value, "and it is not simply the primary under another name");
    }

    [Fact]
    public async Task GetIndicators_ReadsVwapRolling_AtAnAdditionalPeriod()
    {
        // vwap-rolling is the one name whose period argument was in doubt: `vwap` refuses one, and the two
        // sit next to each other in the catalogue. A refusal that keyed on "VWAP-ish" rather than on the
        // anchored instance would make the whole additional-period feature unreachable for the rolling one.
        Composed composed = await ComposeAsync(new IndicatorOptions
        {
            EmaPeriod = PrimaryEma,
            RollingVwapPeriod = 20,
            AdditionalRollingVwapPeriods = "10",
        });

        ToolPayloads.IndicatorSeries selected = await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "vwap-rolling",
            Bucket(0),
            Bucket(SeededBars),
            10,
            cancellationToken: CancellationToken.None);

        selected.Period.Should().Be(10);
        selected.Values.Should().NotBeEmpty();
        selected.Values[0].T.Should().Be(Bucket(9), "a ten-bar window measures from the tenth bar");

        ToolPayloads.IndicatorSeries omitted = await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "vwap-rolling",
            Bucket(0),
            Bucket(SeededBars),
            cancellationToken: CancellationToken.None);

        omitted.Period.Should().Be(20, "twenty is the primary, and omitting the period asks for it");
        omitted.Values[0].T.Should().Be(Bucket(19));
        omitted.Values[^1].V.Should().NotBe(selected.Values[^1].V);
    }

    // ── What a selected period costs ────────────────────────────────────────────────

    [Fact]
    public async Task AnAdditionalPeriodTheStoreHasNoValuesFor_IsProjectedOnTheNextRead_WithNoVendorCall()
    {
        // The operator widened the list after the store was written. Cache-aside must fill the new series
        // from the bars already held -- an empty answer here would read as a market that produced none, and
        // a vendor call would make configuring a second window a billable operation.
        Composed composed = await ComposeAsync(
            read: new IndicatorOptions { EmaPeriod = PrimaryEma, AdditionalEmaPeriods = "3" },
            warmWith: new IndicatorOptions { EmaPeriod = PrimaryEma });

        (await StoredValueAsync("ema", AdditionalEma, Bucket(SeededBars))).Should().BeNull(
            "the warm-up ran without the additional period, or there is nothing left to project");

        ToolPayloads.IndicatorSeries series = await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            AdditionalEma,
            cancellationToken: CancellationToken.None);

        series.Values.Should().NotBeEmpty("the read that first asks for a series is the one that writes it");
        composed.Cache.Projections.Should().Be(1);
        composed.Gateway.BarRequests.Should().Be(
            0, "every bar the new window needs is already local -- the vendor is never called for one");
        composed.Gateway.ContractRequests.Should().Be(0, "resolving a contract is a vendor call too");
    }

    [Fact]
    public async Task AWarmRead_ProbesOnce_HoweverManyPeriodsAreConfigured()
    {
        // The probe is one DISTINCT (Indicator, Period) over the series, so it answers for every configured
        // window at once. A warm read that paid a probe PER PERIOD would make each additional window a
        // standing tax on every call, which is the cost gh#246 bounded.
        Composed composed = await ComposeAsync();

        await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            AdditionalEma,
            cancellationToken: CancellationToken.None);

        composed.Cache.Probes.Should().Be(1);
        composed.Cache.Projections.Should().Be(
            0, "every configured pair is already stored, so a warm read replays nothing");
    }

    [Fact]
    public async Task AConfirmingReplay_IsAnEmptyDiff_WhenTwoPeriodsOfOneIndicatorAreConfigured()
    {
        // Reproducibility (ADR-0006) has to survive the widening: recomputing over the same bars must yield
        // the same numbers for BOTH windows. If reconciliation could not tell the two apart it would remove
        // one of them here, and the count would not be zero.
        Composed composed = await ComposeAsync();

        (await StoredPairsAsync()).Should().Contain(("ema", PrimaryEma)).And.Contain(
            ("ema", AdditionalEma), "an empty diff over one window would prove nothing about two");

        // WRAPPED IN THE TRANSACTION PRODUCTION USES (gh#387), for the reason ComposeAsync states.
        await using IDbContextTransaction replay = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await composed.Projector.ProjectAsync(
            "test", new InstrumentId("ES"), Resolution, SessionStart, CancellationToken.None);

        await _database.SaveChangesAsync();
        await replay.CommitAsync(CancellationToken.None);

        changed.Should().Be(
            0, "a replay over the same bars reproduces the same numbers for every configured period");
    }

    [Fact]
    public async Task APeriodRemovedFromTheList_LeavesItsRowsStanding()
    {
        // Documents the reconcile scope rather than asking for a different one. Reconciliation removes what
        // the CATALOGUE no longer justifies over the bars it can see, and a period dropped from the list
        // leaves rows nothing reads -- the same shape Reconciling_LeavesAnotherPeriodsRowsAlone pins from
        // the projector's side. Nothing here is load-bearing for a caller: the read refuses the period, so
        // the standing rows are unreachable rather than servable.
        Composed composed = await ComposeAsync(
            read: new IndicatorOptions { EmaPeriod = PrimaryEma },
            warmWith: new IndicatorOptions { EmaPeriod = PrimaryEma, AdditionalEmaPeriods = "3" });

        decimal? before = await StoredValueAsync("ema", AdditionalEma, Bucket(SeededBars));
        before.Should().NotBeNull("the warm-up wrote the period that is about to be removed");

        await composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            cancellationToken: CancellationToken.None);

        (await StoredValueAsync("ema", AdditionalEma, Bucket(SeededBars))).Should().Be(
            before, "a read under the narrower catalogue does not sweep the wider one's rows away");

        Func<Task> removed = () => composed.Indicators.GetIndicators(
            "ES",
            Resolution,
            "ema",
            Bucket(0),
            Bucket(SeededBars),
            AdditionalEma,
            cancellationToken: CancellationToken.None);

        (await removed.Should().ThrowAsync<McpException>(
            "the rows stand, but the period is no longer configured -- and answering from them would serve a "
            + "window the operator has stopped maintaining"))
            .Which.Message.Should().Contain("Configured periods");
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
    /// Read straight from the store rather than through <c>get_indicator_at</c>, deliberately: the claim
    /// under test is which period the SNAPSHOT MAP publishes, and reading the comparison number through the
    /// tool that also selects a period would make the assertion turn on that selection being right. What
    /// these tests need from here is only the number the additional window produced, so that the number the
    /// map publishes can be shown to be the other one. The tool's own selection is pinned below.
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
    /// Seeds forty bars under one contract, projects a catalogue over them, and composes the snapshot.
    /// </summary>
    /// <param name="read">
    /// The catalogue the TOOLS read under. Omit for EMA at both <see cref="PrimaryEma"/> and
    /// <see cref="AdditionalEma"/>.
    /// </param>
    /// <param name="warmWith">
    /// The catalogue the seeding replay ran under. Omit for <paramref name="read"/> — pass a different one
    /// to reproduce an operator who widened or narrowed the list after the store was already written.
    /// </param>
    /// <returns>The tools, and the counters a read's cost is measured on.</returns>
    /// <remarks>
    /// <b>One wiring, both tools.</b> The claim is that the batched map and the single as-of read agree about
    /// which period <c>ema</c> means, and two independently wired tools could agree by having been handed the
    /// same fixture twice while disagreeing about the same one.
    /// <para>
    /// <b>Two catalogues, because configuration moves and the store does not.</b> What was projected and what
    /// is being read under are separate facts, and the interesting cases are exactly the ones where they
    /// disagree: a period added after the warm-up must be projected by the read that first asks for it, and a
    /// period removed from the list must leave its rows standing rather than being reconciled away.
    /// </para>
    /// </remarks>
    private async Task<Composed> ComposeAsync(
        IndicatorOptions? read = null,
        IndicatorOptions? warmWith = null)
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
        IndicatorOptions readOptions = read ?? new IndicatorOptions
        {
            EmaPeriod = PrimaryEma,
            AdditionalEmaPeriods = "3",
        };

        IndicatorCatalog catalog = new(Options.Create(readOptions), calendar);
        IndicatorCatalog warmCatalog = warmWith is null
            ? catalog
            : new IndicatorCatalog(Options.Create(warmWith), calendar);

        FakeTimeProvider clock = new(Bucket(SeededBars).AddHours(2));

        // Serves nothing: the window each case reads is filled from the store alone.
        CountingGateway gateway = new([]);

        IndicatorProjector projector =
            new(_database, catalog, NullLogger<IndicatorProjector>.Instance, _telemetry);

        // WRAPPED IN THE TRANSACTION PRODUCTION USES (gh#387). The projector refuses to run outside one, and
        // RepeatableRead is restated by hand because SeriesUnitOfWork, which states it once for production,
        // is internal.
        await using (IDbContextTransaction seed = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None))
        {
            await new IndicatorProjector(
                _database, warmCatalog, NullLogger<IndicatorProjector>.Instance, _telemetry).ProjectAsync(
                "test", new InstrumentId("ES"), Resolution, SessionStart, CancellationToken.None);
            await _database.SaveChangesAsync();
            await seed.CommitAsync(CancellationToken.None);
        }

        InstrumentResolver resolver = new(new InstrumentRegistry(wrapped), new StoreAvailabilityHolder());
        ToolGuards guards = new(wrapped);

        IndicatorCacheService cache = new(
            _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance, _telemetry);

        IndicatorTools indicators = new(
            resolver,
            _database,
            catalog,
            cache,
            gateway,
            guards);

        SnapshotTools snapshot = new(
            new BarTools(
                resolver,
                new BarCacheService(
                    _database, gateway, calendar, projector, clock,
                    NullLogger<BarCacheService>.Instance, _telemetry),
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

        // The seeding replay is the only thing that has run, and it did not go through the gateway or the
        // cache service -- so a test measuring what a READ costs starts from zero on both.
        gateway.ResetCounters();

        return new Composed(snapshot, indicators, cache, gateway, catalog, projector);
    }

    /// <summary>What one wiring hands a test.</summary>
    /// <param name="Snapshot">The composed snapshot read.</param>
    /// <param name="Indicators">The indicator tool, on the same wiring.</param>
    /// <param name="Cache">The cache-aside service both reads trigger, carrying the per-scope counters.</param>
    /// <param name="Gateway">The gateway double, which serves nothing: its counters must stay at zero.</param>
    /// <param name="Catalog">The catalogue the TOOLS read under, which is not always the one that warmed.</param>
    /// <param name="Projector">The projector on that same read catalogue, for a confirming replay.</param>
    private sealed record Composed(
        SnapshotTools Snapshot,
        IndicatorTools Indicators,
        IndicatorCacheService Cache,
        CountingGateway Gateway,
        IndicatorCatalog Catalog,
        IndicatorProjector Projector);
}
