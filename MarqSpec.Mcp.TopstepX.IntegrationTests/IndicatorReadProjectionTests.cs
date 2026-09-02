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
/// A read of an indicator the store holds no values for projects it from the bars already cached, without
/// an operator and without the venue (gh#246).
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier, and only this tier.</b> Every case below fills the store before it reads it, and a fill is a
/// write path. Proving any of these claims therefore means executing the real statements — the bar upsert's
/// <c>ON CONFLICT … DO UPDATE</c> (<c>UpsertBarsSql</c>), the coverage ledger's (<c>RecordCoverageSql</c>)
/// and the projection's value write (<c>UpsertValuesSql</c>) — under the one <c>RepeatableRead</c>
/// transaction <see cref="SeriesUnitOfWork"/> now always opens. The in-memory stand-ins that used to serve
/// these tests in the unit tier were a second implementation of every write, executed by no production
/// process, and they were deleted (gh#387). What is left runs against a real Postgres or it does not run.
/// </para>
/// <para>
/// <b>What "an indicator was added to the catalogue" looks like from the store.</b> The membership of
/// <see cref="IndicatorCatalog"/> is fixed at compile time and only the <i>periods</i> are configurable, so
/// the only way an operator can make the catalogue outrun the store without a code change is to move a
/// period — and the two are the same condition once stored: a <c>(Indicator, Period)</c> pair the catalogue
/// computes for which <c>IndicatorValues</c> holds no row. Nothing in the store can tell them apart, and the
/// trigger under test reads the store. These tests therefore move a period, and that is not a weaker case
/// than adding an indicator; it is the same one, reachable from configuration.
/// </para>
/// <para>
/// <b>The venue is unreachable from this path by construction</b> — <see cref="IndicatorCacheService"/> takes
/// no <see cref="Venue.IMarketDataGateway"/> at all, the same statement <see cref="IndicatorRebuilder"/>
/// makes. The counter assertions below are the second, weaker check; the constructor is the first.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class IndicatorReadProjectionTests : IAsyncLifetime
{
    private const int Resolution = 5;
    private const int SeededBars = 40;

    private static readonly InstrumentId _es = new("ES");

    private readonly SeriesStoreFixture _fixture;
    private readonly TopstepXDbContext _database;
    private readonly CountingGateway _gateway;
    private readonly FakeTimeProvider _clock;

    /// <param name="fixture">The shared container.</param>
    public IndicatorReadProjectionTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();

        _gateway = new CountingGateway(Bars(0, SeededBars));

        // After the last seeded bucket closes, so nothing in the window is still forming.
        _clock = new FakeTimeProvider(Bucket(SeededBars).AddMinutes(Resolution));
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

    /// <summary>
    /// Bars over a half-open index range, drifting irregularly.
    /// </summary>
    /// <remarks>
    /// A tidy ramp produces RSI values like 100 and 50 that survive rounding to the stored scale intact, so a
    /// series built from one hides more than it shows.
    /// </remarks>
    private static IReadOnlyList<Bar> Bars(int fromIndex, int toIndexExclusive) =>
    [
        .. Enumerable.Range(fromIndex, toIndexExclusive - fromIndex).Select(i =>
        {
            decimal drift = i % 3 == 0 ? 1.37m : i % 3 == 1 ? -0.91m : 2.13m;
            decimal close = 5_000m + (i * drift);
            return new Bar(Bucket(i), close, close + 1.25m, close - 0.75m, close, 1_000 + i, "CON.F.US.TEST.Z26");
        }),
    ];

    /// <summary>
    /// Bars over a half-open index range, split across two contracts at <paramref name="rollAt"/>.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <param name="rollAt">The bucket index the second contract starts at.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// The quarter after the first contract, because a roll is what actually puts two contracts on one
    /// series — an arbitrary second string would exercise the same segmentation while describing something
    /// that cannot happen.
    /// </remarks>
    private static IReadOnlyList<Bar> BarsAcrossARoll(int fromIndex, int toIndexExclusive, int rollAt) =>
    [
        .. Bars(fromIndex, toIndexExclusive).Select(bar => bar.OpenTime < Bucket(rollAt)
            ? bar
            : bar with { ContractId = "CON.F.US.TEST.H27" }),
    ];

    // ── The card ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnIndicatorTheStoreHasNoValuesFor_IsProjectedOnTheNextRead_WithNoVendorCall()
    {
        await WarmAsync(Catalog(rsiPeriod: 3));

        // The catalogue now wants RSI at 5. Nothing has ever written (rsi, 5) for this series, and before
        // gh#246 the only thing that ever would was an operator running `rebuild-indicators`.
        IndicatorTools tools = Tools(Catalog(rsiPeriod: 5));
        _gateway.ResetCounters();

        ToolPayloads.IndicatorSeries series = await tools.GetIndicators(
            "ES", Resolution, "rsi", Bucket(0), Bucket(SeededBars), CancellationToken.None);

        series.Period.Should().Be(5, "the read must answer under the period the catalogue is configured for");
        series.Values.Should().NotBeEmpty(
            "the bars are already cached, so the value is computable without an operator and without the "
            + "venue -- an absence here would be an artefact of when computation happened rather than a fact "
            + "about the market");
        _gateway.BarRequests.Should().Be(0, "no bar is missing, so nothing may be fetched");
        _gateway.ContractRequests.Should().Be(0, "resolving a contract is a vendor call too");
    }

    [Fact]
    public async Task TheReadTriggeredProjection_ProducesWhatARebuildWouldHave()
    {
        // Reproducibility is the property ADR-0006 protects, and the trigger change must leave it exactly
        // where it was: what a read computes and what a replay computes are the same numbers, so a rebuild
        // immediately after a read-triggered projection is an EMPTY DIFF.
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCatalog wider = Catalog(rsiPeriod: 5);
        IndicatorTools tools = Tools(wider);

        ToolPayloads.IndicatorSeries fromRead = await tools.GetIndicators(
            "ES", Resolution, "rsi", Bucket(0), Bucket(SeededBars), CancellationToken.None);

        // WRAPPED IN THE TRANSACTION PRODUCTION USES (gh#387). The projector refuses to run outside one --
        // it writes its values with a statement the store runs as it is sent, while its removals wait for
        // SaveChanges -- and that guard used to be skipped for the in-memory provider, so this seeding helper
        // had never once run the shape the server runs. RepeatableRead is restated by hand because
        // SeriesUnitOfWork, which states it once for production, is internal.
        await using IDbContextTransaction replay = await _database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await new IndicatorProjector(_database, wider, NullLogger<IndicatorProjector>.Instance)
            .ProjectAsync("test", _es, Resolution, _clock.GetUtcNow(), CancellationToken.None);
        await _database.SaveChangesAsync();
        await replay.CommitAsync(CancellationToken.None);

        changed.Should().Be(0, "a replay over the same bars reproduces the same numbers, so it rewrites none");
        fromRead.Values.Should().NotBeEmpty("otherwise the empty diff above would prove nothing");
    }

    [Fact]
    public async Task AnIndicatorTheStoreAlreadyCovers_IsNotReprojected()
    {
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "every pair the catalogue computes already has values, so a read must cost the probe and nothing "
            + "more -- a warm series that replays itself on every call is the cost this card was told to bound");
        indicators.Projections.Should().Be(0);
        indicators.Probes.Should().Be(1);
    }

    [Fact]
    public async Task ASeriesShorterThanTheWarmUp_IsNotProjected_AndTheAbsenceStands()
    {
        // A missing number is missing, never a default -- and never a reason to replay on every read either.
        // Six bars cannot satisfy MACD's signal warm-up of 34, so the absence is a fact about the bars. A
        // trigger keyed on "this pair has no rows" alone would project this series forever and never write a
        // value, because there is no value to write.
        await WarmAsync(Catalog(rsiPeriod: 3), bars: 6);

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "no pair the store lacks is one these bars could satisfy, so there is nothing to compute");

        IndicatorTools tools = Tools(Catalog(rsiPeriod: 3));
        ToolPayloads.IndicatorSeries series = await tools.GetIndicators(
            "ES", Resolution, "macd-signal", Bucket(0), Bucket(6), CancellationToken.None);

        series.Values.Should().BeEmpty("thirty-four bars are needed and six are stored");
    }

    [Fact]
    public async Task ASeriesWithNoBars_IsNotProjected()
    {
        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse(
            "an unknown instrument, or one nothing has ever fetched, must not open a transaction to compute "
            + "nothing from no bars");
    }

    [Fact]
    public async Task OneSeriesIsProbedOncePerScope_HoweverManyIndicatorsAreRead()
    {
        // get_market_snapshot asks GetIndicatorAt once per indicator per resolution -- eleven times over the
        // same series -- and every one of those would otherwise re-ask the store whether the series is
        // complete. The scope is the request, and within it a series that was complete stays complete:
        // nothing writes a bar without projecting over it in the same unit of work.
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3));

        for (int i = 0; i < 11; i++)
        {
            await indicators.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        }

        indicators.Probes.Should().Be(1, "the answer is memoised for the life of the scope");
    }

    [Fact]
    public async Task AWarmRead_DoesNotIncrementTheProcessReplayCounter()
    {
        // The probe found nothing missing, so nothing opened a replay. A counter that ticked on every
        // EnsureProjectedAsync would make a warm process look like a cold one, and startup warmup would
        // have no way to tell the two apart (gh#347).
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorReadProjectionCounter counter = new();
        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 3), counter);

        bool projected = await indicators.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projected.Should().BeFalse();
        counter.Replays.Should().Be(0, "a complete series must not count as a read-triggered replay");
        indicators.Projections.Should().Be(0);
    }

    [Fact]
    public async Task AColdRead_IncrementsTheProcessReplayCounterOnce_EvenAcrossElevenCallsInOneScope()
    {
        // get_market_snapshot asks get_indicator_at eleven times over one series. The scope memo already
        // collapses those to one replay; the process counter must follow that, not the call count, and it
        // must outlive the scope so a later request can read it without scraping a log (gh#347).
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorReadProjectionCounter counter = new();
        IndicatorCacheService first = Cache(Catalog(rsiPeriod: 5), counter);

        for (int i = 0; i < 11; i++)
        {
            await first.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        }

        first.Projections.Should().Be(1, "the memo still collapses eleven reads of one series to one replay");
        counter.Replays.Should().Be(1, "one series, one request, one increment — readable as a field");

        IndicatorCacheService second = Cache(Catalog(rsiPeriod: 5), counter);
        bool projectedAgain = await second.EnsureProjectedAsync(
            "test", _es, Resolution, CancellationToken.None);

        projectedAgain.Should().BeFalse("the first scope already wrote the missing pair");
        second.Projections.Should().Be(0);
        counter.Replays.Should().Be(1, "the count is process-lifetime, not per-scope, and a warm read adds none");
    }

    [Fact]
    public async Task GetIndicatorAt_ProjectsToo_NotOnlyGetIndicators()
    {
        // Both indicator reads are on the trigger, and get_indicator_at is the one get_market_snapshot uses.
        // A trigger wired to only one of them would leave the composed read -- the tool an agent actually
        // reaches for -- reporting cannot-measure over bars that measure perfectly well.
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorTools tools = Tools(Catalog(rsiPeriod: 5));
        _gateway.ResetCounters();

        ToolPayloads.IndicatorReading reading = await tools.GetIndicatorAt(
            "ES", Resolution, "rsi", Bucket(SeededBars), CancellationToken.None);

        reading.Value.Should().NotBeNull();
        _gateway.BarRequests.Should().Be(0);
    }

    [Fact]
    public async Task ASeriesWhoseEveryContractRunIsShorterThanTheWarmUp_ReplaysOnEveryRead()
    {
        // THE RESIDUE OF THE WARM-UP BOUND, PINNED RATHER THAN ASSERTED -- and it is reachable with ONE roll,
        // not with the implausible number the first version of this claim said. The bound counts the WHOLE
        // stored series while warm-up restarts at every contract seam, and the stored series is whatever was
        // fetched rather than a complete contract run: BarCacheService writes only the outstanding buckets,
        // and ContractRollDetector.Segment splits purely on a ContractId change. So two ordinary get_bars
        // windows either side of one quarterly roll are enough.
        //
        // Twelve bars under each of two contracts, at the SHIPPED periods. Seven pairs pass `WarmupBars <= 24`
        // -- atr(14) and rsi(14) at 15, sma(20), ema(20) and the three Bollinger bands at 20 -- and not one of
        // them can ever be produced from a twelve-bar run. Only vwap, whose warm-up is 1, gets rows.
        //
        // Nothing is WRONG here: the pass writes nothing, the absences are the honest answer, and the series
        // is by construction tiny. What it costs is a transaction and a replay on every read, forever. It is
        // recorded rather than guarded, and this test is what stops that decision being a guess.
        // Written straight to the store. A history call answers for exactly ONE contract and CountingGateway
        // stamps every bar it serves with the one it resolved, as a real gateway must (ADR-0011) -- so two
        // contracts cannot reach the store through a single fill. Two fills either side of the roll is what
        // puts them there, and this is that state.
        await SeedDirectlyAsync(BarsAcrossARoll(0, 24, 12));

        IndicatorCacheService first = Cache(ShippedCatalog());
        IndicatorCacheService second = Cache(ShippedCatalog());
        IndicatorCacheService third = Cache(ShippedCatalog());

        bool one = await first.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        bool two = await second.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);
        bool three = await third.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);

        new[] { one, two, three }.Should().AllBeEquivalentTo(
            true,
            "each read finds the same seven pairs missing, replays the series, and produces none of them -- "
            + "so the next read finds them missing again. ADR-0014 and IndicatorCacheService both describe "
            + "this, and both said it needed more rolls than a quarterly contract can have; it needs one");

        (await StoredPairsAsync()).Should().Equal(
            [("vwap", 0)],
            "the only warm-up a twelve-bar run satisfies is VWAP's, which is 1. If this ever grows, the "
            + "series stops re-replaying and the paragraph this test pins is describing nothing");
    }

    [Fact]
    public async Task StoredPairs_AreOrderedByIndicatorThenPeriod_WhenOneIndicatorIsStoredAtTwoPeriods()
    {
        // gh#432. StoredPairsAsync's query is an unordered SELECT DISTINCT, and its client-side sort orders
        // by indicator name only -- so two rows that share a name and differ only in period are never
        // compared to each other, and come back in whatever order the database happened to hand them.
        //
        // A single-element fixture cannot observe that: with one row there is no tie for an incomplete sort
        // to mis-order. This file's own dominant idiom manufactures one instead -- warm at rsiPeriod 3, then
        // read at rsiPeriod 19. ReconcileAsync (IndicatorProjector.cs:278) deliberately leaves a stored row
        // the new catalogue does not own standing rather than deleting it, so (rsi, 3) survives sitting
        // beside the freshly projected (rsi, 19). Every other pair keeps the same period across both
        // catalogues, so rsi is the only tie -- exactly the shape the helper cannot resolve today.
        //
        // Period 19, not the file's usual 5: on the twelve rows this fixture seeds, Postgres's planner
        // prefers an index scan over the composite index on (Instrument, ResolutionMinutes, Indicator,
        // Period, BucketStart), which returns pre-sorted rows for free regardless of which two periods tie
        // -- 3-and-5 included. StoredPairsUnderHashAggregateAsync forces the Seq Scan + HashAggregate plan
        // the store gets once an index scan is no longer the cheaper way to deduplicate, and 3-and-19 is the
        // pair verified (repeated fresh-container runs, not a single lucky one) to land in different
        // HashAggregate buckets in an order that is NOT already ascending on this build -- 3-and-5 happened
        // to still come out sorted even under that forced plan. Either pair is equally a tie; 19 is the one
        // that actually exercises the missing comparison instead of passing by looking like it does.
        await WarmAsync(Catalog(rsiPeriod: 3));

        IndicatorCacheService indicators = Cache(Catalog(rsiPeriod: 19));
        bool projected = await indicators.EnsureProjectedAsync("test", _es, Resolution, CancellationToken.None);

        projected.Should().BeTrue("rsi at period 19 has never been computed for this series");

        IReadOnlyList<(string Indicator, int Period)> pairs = await StoredPairsUnderHashAggregateAsync();

        pairs.Should().Contain(("rsi", 3)).And.Contain(("rsi", 19),
            "the tie this test exists to create -- one indicator name stored at two periods at once -- must "
            + "actually be present, or the assertion below is not testing the case it is named for");

        // Asserted as a PROPERTY -- the sequence equals its own sort by (indicator, period) -- rather than as
        // a literal list. The shipped catalogue's other members (atr, sma, ema, the three Bollinger bands,
        // the three MACD lines, vwap) are not this test's concern and would make a literal brittle to a
        // catalogue change; what this test pins is that no two rows are ever out of order relative to each
        // other, which a literal naming only the tied pair would not check for the untied rows either.
        List<(string Indicator, int Period)> orderedByIndicatorThenPeriod =
            [.. pairs.OrderBy(p => p.Indicator, StringComparer.Ordinal).ThenBy(p => p.Period)];

        pairs.Should().Equal(
            orderedByIndicatorThenPeriod,
            "the sort must be total: two rows sharing an indicator name still have a period, and that period "
            + "must break the tie so the sequence the helper returns is already in its own sorted order");
    }

    /// <summary>
    /// Calls <see cref="StoredPairsAsync"/> under a planner shape forced onto Seq Scan + HashAggregate for
    /// the <c>DISTINCT</c> -- the shape <c>IndicatorValues</c> gets once it holds enough rows that an index
    /// scan is no longer the cheaper way to deduplicate.
    /// </summary>
    /// <returns>The pairs, fetched under that plan.</returns>
    /// <remarks>
    /// On the handful of rows this fixture seeds, Postgres's own cost model instead picks an index scan over
    /// the composite index on <c>(Instrument, ResolutionMinutes, Indicator, Period, BucketStart)</c>, which
    /// returns rows pre-sorted by (Indicator, Period) for free -- an accident of a plan a real store outgrows,
    /// not the sort this test means to exercise. The settings are session-scoped (<c>SET LOCAL</c>) and the
    /// transaction is rolled back rather than committed: nothing here writes a row, and neither the setting
    /// nor an accidental write may leak into the next test on this shared container.
    /// </remarks>
    /// <exception cref="Xunit.Sdk.XunitException">
    /// The forcing below did not reach the session the query runs on. This is the one thing that makes the
    /// test above able to see its case at all -- without it, the fixture is the tenth one gh#432 warned
    /// about, red today only by an accident of a plan this small table happens to get. Asserted rather than
    /// merely relied on, the same discipline the QA contract already requires of a <c>DbCommandInterceptor</c>:
    /// "assert the interceptor actually fired -- one that never did passes by exercising nothing."
    /// </exception>
    private async Task<IReadOnlyList<(string Indicator, int Period)>> StoredPairsUnderHashAggregateAsync()
    {
        await using IDbContextTransaction probe = await _database.Database
            .BeginTransactionAsync(CancellationToken.None);
        await _database.Database.ExecuteSqlRawAsync(
            "SET LOCAL enable_sort = off; SET LOCAL enable_indexscan = off; "
            + "SET LOCAL enable_indexonlyscan = off; SET LOCAL enable_bitmapscan = off;",
            CancellationToken.None);

        string indexScan = (await _database.Database
            .SqlQuery<string>($"SELECT current_setting('enable_indexscan') AS \"Value\"")
            .ToListAsync(CancellationToken.None)).Single();
        indexScan.Should().Be("off", "the plan-forcing above must actually be in effect on this session, "
            + "or the query below runs under whatever plan Postgres would have picked anyway and this test "
            + "stops testing the case it exists for");

        IReadOnlyList<(string Indicator, int Period)> pairs = await StoredPairsAsync();

        await probe.RollbackAsync(CancellationToken.None);
        return pairs;
    }

    // ── Scaffolding ──────────────────────────────────────────────────────────────────────────────────

    private static BarSessionCalendar Calendar() => BarSessionCalendar.Parse("16:00", []);

    /// <summary>
    /// The catalogue, with RSI at a chosen period.
    /// </summary>
    /// <param name="rsiPeriod">The RSI period this catalogue computes at.</param>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// ATR is pinned short so a forty-bar series produces values at all; RSI is the knob these tests move,
    /// because moving it is how a pair the store has never held becomes one the catalogue computes.
    /// </remarks>
    /// <summary>
    /// The catalogue at the periods this server actually ships, so warm-ups are the real ones.
    /// </summary>
    /// <returns>The catalogue.</returns>
    /// <remarks>
    /// The other tests here pin ATR short so a forty-bar series produces values at all. The residue test
    /// needs the opposite — warm-ups long enough that a twelve-bar contract run cannot satisfy them — and
    /// the shipped defaults already are, so it uses them rather than inventing a period nobody runs.
    /// </remarks>
    private static IndicatorCatalog ShippedCatalog() =>
        new(Options.Create(new IndicatorOptions()), Calendar());

    /// <summary>Writes bars straight to the store, bypassing the fill path and its single contract.</summary>
    /// <param name="bars">The bars.</param>
    private async Task SeedDirectlyAsync(IReadOnlyList<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = _es.Symbol,
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
    }

    /// <summary>The distinct <c>(Indicator, Period)</c> pairs the store holds for the seeded series.</summary>
    /// <returns>The pairs, ordered by indicator name and then by period -- a total order. Two rows that
    /// share an indicator name (a period the current catalogue no longer owns, left standing by
    /// <c>ReconcileAsync</c>, beside the one it now computes) are broken apart by period rather than left
    /// in whatever order the database happened to return them.</returns>
    /// <remarks>
    /// The order is stated in the query -- <c>SELECT DISTINCT indicator, period ... ORDER BY indicator,
    /// period</c> -- rather than imposed afterwards on the materialised list. A <c>SELECT DISTINCT</c> with
    /// no <c>ORDER BY</c> is free to come back in whatever order a HashAggregate plan produces, so ordering
    /// only the in-memory result would still be pairing an unordered fetch with a sort that has to be
    /// re-proven correct by inspection; asking the database for the order it is already computing states
    /// the intent where the next reader meets it and needs no comparer of its own. This depends on the
    /// database's collation, not on the vocabulary being simple: <c>pg_database.datcollate</c> is
    /// <c>C.UTF-8</c> with <c>datlocprovider = 'c'</c> on the <c>timescale/timescaledb-ha:pg17</c> image this
    /// suite runs (measured, not assumed), and <c>Indicator</c> carries no column-level collation override
    /// -- so the ordering is a raw byte comparison, identical to <see cref="StringComparer.Ordinal"/> for
    /// any string, hyphens (<c>bb-lower</c>, <c>macd-signal</c>) included. A vocabulary-shaped argument (all
    /// lowercase, all ASCII) would still be wrong for punctuation under most locale-aware collations and
    /// would silently stop holding the moment either changed; this does not turn on either.
    /// </remarks>
    private async Task<IReadOnlyList<(string Indicator, int Period)>> StoredPairsAsync()
    {
        var held = await _database.IndicatorValues
            .Where(v => v.Venue == "test"
                && v.Instrument == _es.Symbol
                && v.ResolutionMinutes == Resolution)
            .Select(v => new { v.Indicator, v.Period })
            .Distinct()
            .OrderBy(v => v.Indicator)
            .ThenBy(v => v.Period)
            .ToListAsync();

        return [.. held.Select(v => (v.Indicator, v.Period))];
    }

    private static IndicatorCatalog Catalog(int rsiPeriod) =>
        new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = rsiPeriod }),
            Calendar());

    private static IOptions<MarketDataOptions> MarketData() =>
        Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            SessionCloseCentral = "16:00",
        });

    /// <summary>Fills the store the way the world fills it — through the cache-aside bar read.</summary>
    /// <param name="catalog">The catalogue in force while the bars were written.</param>
    /// <param name="bars">How many buckets to warm.</param>
    private async Task WarmAsync(IndicatorCatalog catalog, int bars = SeededBars)
    {
        BarCacheService cache = new(
            _database,
            _gateway,
            Calendar(),
            new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
            _clock,
            NullLogger<BarCacheService>.Instance);

        BarReadResult warmed = await cache.GetBarsAsync(
            _es, Resolution, new BarRange(Bucket(0), Bucket(bars)), CancellationToken.None);

        warmed.Bars.Should().HaveCount(bars, "the rest of each test rests on the store being warm");
    }

    private IndicatorCacheService Cache(
        IndicatorCatalog catalog,
        IndicatorReadProjectionCounter? readTriggeredReplays = null) =>
        new(_database, catalog, new IndicatorProjector(_database, catalog, NullLogger<IndicatorProjector>.Instance),
            _clock, NullLogger<IndicatorCacheService>.Instance, readTriggeredReplays);

    private IndicatorTools Tools(IndicatorCatalog catalog) =>
        new(
            new InstrumentResolver(new InstrumentRegistry(MarketData()), new StoreAvailabilityHolder()),
            _database,
            catalog,
            Cache(catalog),
            _gateway,
            new ToolGuards(MarketData()));
}
