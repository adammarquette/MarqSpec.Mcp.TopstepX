using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The <c>reselect-bars</c> loop — re-deciding stored provenance over an operator-named window (gh#506).
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the unit tier because every claim below is about <b>rows a raw-SQL statement
/// wrote</b>: the winner's upsert, the loser's <c>ExecuteDelete</c>, the coverage sweep and the projection
/// that follows them all run inside one <c>SeriesUnitOfWork</c>, which the in-memory provider cannot
/// represent at all (gh#387).
/// </para>
/// <para>
/// The fixture shape is <c>HistoricalContractSelectionTests</c>' — a June trade date on <c>HMUZ</c> names
/// <c>M26</c> and <c>U26</c>, so the venue's own pick is one of the two candidates and can be shown
/// <i>losing</i> rather than merely being absent. Every instant is a literal, for the reason that suite
/// gives: derived from <c>BarCacheService.PresentHorizon</c> an expectation would follow the constant and
/// the case that exists to catch a change to it would stay green.
/// </para>
/// <para>
/// <b>No tenure anchor is seeded, and that is the subject.</b> A reselect treats the whole window as
/// history whatever the store's trailing run says, so a suite that had to arrange the present band would be
/// testing the read path's question instead of this one.
/// </para>
/// </remarks>
[Collection(SeriesStoreCollection.Name)]
public sealed class BarReselectorTests : IAsyncLifetime
{
    /// <summary>The contract the venue marks active — thin over the June window below.</summary>
    private const string Front = "CON.F.US.MES.U26";

    /// <summary>The contract that carried June's volume, and therefore the window's winner.</summary>
    private const string Liquid = "CON.F.US.MES.M26";

    private static readonly InstrumentId _mes = new("MES");

    private readonly SeriesStoreFixture _fixture;

    private readonly TopstepXDbContext _database;

    /// <param name="fixture">The shared container.</param>
    public BarReselectorTests(SeriesStoreFixture fixture)
    {
        _fixture = fixture;
        _database = fixture.CreateContext();
    }

    /// <summary>A Tuesday mid-session in June 2026, the first bucket of every window below.</summary>
    private static DateTimeOffset JuneStart => Market(2026, 6, 16, 9);

    /// <summary>The instant the operator runs the verb at — two months past the window, so nothing forms.</summary>
    private static DateTimeOffset Now => Market(2026, 8, 18, 12);

    /// <summary>
    /// The one-hour, twelve-bucket window the cases below <b>ask</b> for — deliberately a clipping one.
    /// </summary>
    /// <remarks>
    /// It covers part of one trade date and no whole one, which is the shape an operator actually types. What
    /// gets re-decided is <see cref="Session"/>, and the difference between the two is the subject of
    /// <c>AClippingWindow_IsWidenedToWholeTradeDates_SoNoDayIsSpliced</c>.
    /// </remarks>
    private static BarRange Window => new(JuneStart, JuneStart.AddHours(1));

    /// <summary>The whole session <see cref="Window"/> falls inside — the effective window.</summary>
    /// <remarks>
    /// A literal, and the two ends are the calendar's own: the session for trade date 2026-06-16 opens at
    /// 17:00 Central the evening before and closes at 16:00 Central on the day.
    /// </remarks>
    private static BarRange Session => new(Market(2026, 6, 15, 17), Market(2026, 6, 16, 16));

    /// <summary>The session calendar every fixture here shares.</summary>
    private static BarSessionCalendar Calendar => BarSessionCalendar.Parse("16:00", []);

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AClippingWindow_IsWidenedToWholeTradeDates_SoNoDayIsSpliced()
    {
        // AN OPERATOR TYPES A WINDOW; THE POLICY DECIDES A TRADE DATE. Taken literally, an hour-long window
        // re-decides the whole of 2026-06-16 from one hour of volume and then rewrites only that hour --
        // leaving U26 … M26 … U26 inside a single day, which is exactly the interleaving
        // HistoricalContractPolicy exists to forbid. ContractRollDetector would cut the day into three
        // segments, every indicator would warm again at each seam, and TradeDatesChanged would report that
        // the date had moved when most of it had not.
        //
        // So the effective window is the union of the whole trade dates the asked window intersects, and
        // everything -- plan, fetch, decision, upsert, delete and count -- happens over that.
        await SeedManyAsync(Front, SessionBars(Thin));

        CountingGateway gateway = Venue(SessionBars(Fat), SessionBars(Thin));
        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        result.EffectiveWindow.Start.Should().Be(Session.Start, "the session opens the evening before");
        result.EffectiveWindow.End.Should().Be(Session.End);

        List<BarRecord> day = await StoredAsync(Session.Start, Session.End);
        day.Should().HaveCount(276, "twenty-three hours of five-minute buckets");
        day.Should().OnlyContain(
            row => row.ContractId == Liquid, "the whole trade date went to the contract that carried it");

        IReadOnlyList<ContractSegment> segments =
            ContractRollDetector.Segment([.. day.Select(IndicatorProjector.ToBar)]);
        segments.Should().ContainSingle("nothing rolled on 2026-06-16, so the day is one run");

        result.TradeDatesChanged.Should().Be(1);
    }

    [Fact]
    public async Task ThinBarsStoredUnderTheWrongFront_AreReplacedByTheWinner()
    {
        // The defect ADR-0020 §5 leaves standing on the read path: a June window fetched before gh#505
        // was answered by the venue's own pick, and every row in it now carries U26's thin series. A read
        // will never rewrite it -- an attributed bucket is kept, which is what keeps a warm read
        // byte-identical -- so the operator's verb is the only thing that can.
        await SeedWindowAsync(Front, Thin);

        CountingGateway gateway = Venue(FatLiquid(), ThinFront());
        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        List<BarRecord> stored = await StoredAsync(Window.Start, Window.End);
        stored.Should().HaveCount(12);
        stored.Should().OnlyContain(
            row => row.ContractId == Liquid,
            "the contract that carried the volume is the one the window is now attributed to");
        stored.Should().OnlyContain(
            row => row.Close == 205m && row.Volume == 5_000,
            "the winner's OHLCV moves with its contract id -- a row whose provenance describes a different "
            + "observation from the one it holds is worse than no provenance at all");

        result.BarsRevised.Should().Be(12, "every bucket in the window changed hands");
        result.TradeDatesChanged.Should().Be(1);
        result.Ties.Should().Be(0);
        result.SeriesSkipped.Should().Be(0);
        result.SlicesSkipped.Should().Be(0);
        result.VenueRequests.Should().Be(2, "one page per candidate, and the window is one page wide");
    }

    [Fact]
    public async Task ATie_IsCounted_AndGoesToTheNearerExpiry()
    {
        // Decide carries no tie flag; VolumeByContract is the only evidence there was one, and a tie is
        // exactly the case where "the volume decided it" is a claim an operator should not be left to
        // assume. The break is the nearer expiry, so a tie still lands somewhere reproducible.
        await SeedWindowAsync(Front, Thin);

        // The same volume under both candidates -- the prices still differ, so the write is observable.
        CountingGateway gateway = Venue(
            Bars(bucket => Fat(bucket) with { Volume = 1_000 }),
            Bars(bucket => Thin(bucket) with { Volume = 1_000 }));

        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        result.Ties.Should().Be(1, "the top volume was shared, and the summary has to say so");

        List<BarRecord> stored = await StoredAsync(Window.Start, Window.End);
        stored.Should().OnlyContain(
            row => row.ContractId == Liquid, "a tie goes to the nearer expiry, never to insertion order");
    }

    [Fact]
    public async Task ARunOutsideTheWindow_IsUntouched()
    {
        // The window is the operator's whole authority. A row outside it was not re-decided, so rewriting
        // it would be revising provenance on the strength of not having looked.
        await SeedWindowAsync(Front, Thin);
        await SeedBarAsync(Front, Thin(Market(2026, 8, 18, 9)));

        CountingGateway gateway = Venue(FatLiquid(), ThinFront());
        await Reselector(gateway).ReselectAsync(_mes, Window, default);

        List<BarRecord> outside =
            await StoredAsync(Market(2026, 8, 18, 9), Market(2026, 8, 18, 10));
        outside.Should().ContainSingle();
        outside[0].ContractId.Should().Be(Front, "nothing outside the window was re-decided");
        outside[0].Close.Should().Be(100.5m);
    }

    [Fact]
    public async Task RowsTheWinnerDoesNotRestate_AreRemoved_OnlyInsideTheWindow()
    {
        // The bucket key is (Venue, Instrument, ResolutionMinutes, BucketStart) and carries no contract, so
        // the winner's upsert REPLACES a loser's row in place wherever the winner has a bar. What it cannot
        // touch is a bucket only the loser answered: left standing, that row keeps a thin contract's numbers
        // inside a window the store now says belongs to another contract.
        //
        // And the EFFECTIVE window is the whole authority. The row seeded on the next trade date belongs to
        // a day this run never re-decided -- deleting it would be revising provenance on the strength of not
        // having looked, which is the argument SessionBarService's reconcile makes for an explicit list. It
        // has to be another trade date rather than merely another hour, because an hour outside the asked
        // window is still inside the effective one.
        await SeedWindowAsync(Front, Thin);
        await SeedBarAsync(Front, Thin(Market(2026, 6, 17, 9)));

        CountingGateway gateway = Venue(Bars(Fat).Take(11), ThinFront());
        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        result.BarsRemoved.Should().Be(1, "one bucket in the window the winner has no bar for");
        result.BarsRevised.Should().Be(11);

        List<BarRecord> inside = await StoredAsync(Window.Start, Window.End);
        inside.Should().HaveCount(11);
        inside.Should().OnlyContain(row => row.ContractId == Liquid);

        List<BarRecord> outside = await StoredAsync(Market(2026, 6, 17, 9), Market(2026, 6, 17, 10));
        outside.Should().ContainSingle();
        outside[0].ContractId.Should().Be(Front, "a row outside the effective window was never re-decided");
    }

    [Fact]
    public async Task AnUnattributedBucketTheWinnerDoesNotRestate_IsCountedApart()
    {
        // A bucket with no contract id is not a loser, it is a row written before the provenance migration
        // (gh#402). It goes for the same reason a loser's does -- the winner does not restate it, and it
        // would sit inside a re-decided day carrying numbers nobody can attribute -- but folding it into
        // barsRemoved would report an operator that a contract lost buckets it never held.
        await SeedWindowAsync(Front, Thin);
        await SeedBarAsync(null, Thin(JuneStart.AddMinutes(65)));

        CountingGateway gateway = Venue(FatLiquid(), ThinFront());
        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        result.UnattributedRemoved.Should().Be(1, "the bucket the migration could not attribute");
        result.BarsRemoved.Should().Be(0, "no contract lost a bucket it actually held");

        List<BarRecord> stored = await StoredAsync(Session.Start, Session.End);
        stored.Should().HaveCount(12);
        stored.Should().OnlyContain(row => row.ContractId == Liquid);
    }

    [Fact]
    public async Task ASliceWithNoListedCandidate_IsSkipped_AndCounted()
    {
        // A March window on HMUZ at depth two names H26 and M26, and this venue lists neither. The read path
        // falls back to the venue's own pick and keeps serving; a rewrite must not, because attributing a
        // trade date to a contract chosen by degradation is worse than leaving the rows alone. The slice is
        // skipped, counted, and the venue is never asked for a bar.
        BarRange march = new(Market(2026, 3, 17, 9), Market(2026, 3, 17, 10));
        await SeedBarAsync(Front, Thin(march.Start));

        CountingGateway gateway = new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal) { [Front] = [] }, Front);

        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, march, default);

        result.SlicesSkipped.Should().Be(1);
        result.BarsRevised.Should().Be(0);
        result.BarsRemoved.Should().Be(0);
        gateway.BarRequests.Should().Be(0, "a slice nobody could answer properly is never fetched");

        List<BarRecord> stored = await StoredAsync(march.Start, march.End);
        stored.Should().ContainSingle();
        stored[0].ContractId.Should().Be(Front, "the row is left exactly as it was");
    }

    [Fact]
    public async Task ACandidateChangeOnAHolidayEve_StillDecidesEachTradeDateOnce()
    {
        // The planner cuts a window where the cycle's nearest candidate moves, and on a holiday eve that cut
        // lands at UTC midnight rather than at a session open -- because TradeDateFor answers nothing for a
        // declared holiday and the UTC-date fallback stands in. A venue that answers a little beyond the
        // slice it was asked for (a page boundary, a vendor rounding outward -- the behaviour
        // ConcurrencyHarness already models) then puts bars for the SAME fallback date on both sides of that
        // cut. Deciding per slice yields two selections for one trade date, and the run dies assembling its
        // own winners dictionary.
        //
        // So the candidates are merged across every slice of the window and the policy is asked once.
        BarSessionCalendar holidayEve = BarSessionCalendar.Parse("16:00", ["2026-07-01"]);
        BarRange straddling = new(Market(2026, 6, 30, 9), Market(2026, 7, 2, 10));

        await SeedManyAsync(Front, Bars(Thin, Market(2026, 6, 30, 9), 12));

        CountingGateway gateway = Venue(
            Bars(Fat, Market(2026, 6, 30, 9), 12),
            [.. Bars(Thin, Market(2026, 6, 30, 9), 12), .. Bars(Thin, Market(2026, 7, 1, 9), 12)]);
        gateway.AnswersBeyondTheSlice = true;

        BarReselectResult result = await Reselector(gateway, calendar: holidayEve)
            .ReselectAsync(_mes, straddling, default);

        result.BarsRevised.Should().Be(24, "June the thirtieth changes hands, and July the first is new");

        List<BarRecord> june = await StoredAsync(Market(2026, 6, 30, 9), Market(2026, 6, 30, 10));
        june.Should().HaveCount(12);
        june.Should().OnlyContain(row => row.ContractId == Liquid);

        List<BarRecord> holiday = await StoredAsync(Market(2026, 7, 1, 9), Market(2026, 7, 1, 10));
        holiday.Should().HaveCount(12);
        holiday.Should().OnlyContain(
            row => row.ContractId == Front, "only one candidate answered the holiday's buckets");
    }

    [Fact]
    public async Task ADeleteOnlyReselect_StillProjects()
    {
        // The read path projects only `if (written > 0)`, and copying that guard here would be a defect
        // wearing the fix's clothes: a pass whose upserts are all no-ops but which DELETED a row leaves
        // every indicator value standing over a bar that is gone.
        //
        // The seeded shape puts the loser's row in the MIDDLE of the winner's run, so the removal is
        // observable in the projection rather than only in the bar table: before, the contract changes twice
        // and ContractRollDetector cuts three segments, each warming ATR(3) separately; after, it is one
        // eleven-bar segment and the values the two seams suppressed appear.
        IEnumerable<Bar> winner = Bars(Fat).Where(bar => bar.OpenTime != JuneStart.AddMinutes(25));
        await SeedBarsAsync(Liquid, winner);
        await SeedBarAsync(Front, Thin(JuneStart.AddMinutes(25)));
        await RebuildAsync();

        List<DateTimeOffset> before = await AtrBucketsAsync();
        before.Should().HaveCount(5, "three segments, and only two of them are long enough to warm ATR(3)");

        CountingGateway gateway = Venue(winner, [Thin(JuneStart.AddMinutes(25))]);
        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        result.BarsRevised.Should().Be(0, "the store already agrees with the winner, bar for bar");
        result.BarsRemoved.Should().Be(1);

        List<DateTimeOffset> after = await AtrBucketsAsync();
        after.Should().HaveCount(
            8, "the seam is gone, so the projection ran over one segment and warmed once");
        after.Should().Contain(JuneStart.AddMinutes(30), "a value the removed seam used to suppress");
    }

    [Fact]
    public async Task TheVerb_ReprojectsIndicators_SoNoValueSurvivesASeamItCrossed()
    {
        // Indicators are projections over the bars (ADR-0006), so a verb that rewrites the bars and leaves
        // the values standing has published numbers nobody can reproduce from the series they describe.
        await SeedWindowAsync(Front, Thin);
        await RebuildAsync();

        List<decimal> before = await AtrValuesAsync();
        before.Should().NotBeEmpty("the case needs values to have existed over the thin series");
        before.Should().OnlyContain(value => value == 2m, "the thin series' true range is a flat two");

        CountingGateway gateway = Venue(FatLiquid(), ThinFront());
        await Reselector(gateway).ReselectAsync(_mes, Window, default);

        List<decimal> after = await AtrValuesAsync();
        after.Should().NotBeEmpty();
        after.Should().OnlyContain(
            value => value == 20m,
            "every value standing over the window was recomputed from the winner's bars");
    }

    [Fact]
    public async Task AWindowTooWideForOnePass_SkipsThatSeries_AndSaysSo()
    {
        // BarGapDetector refuses to enumerate more than MaxBucketsPerPass, and a reselect over a window
        // wider than that would page the venue for a candidate set nothing here can bound. The series is
        // skipped rather than trimmed: the window is the operator's statement of what they mean to rewrite,
        // and quietly reselecting the first part of it would report a number about a smaller question than
        // the one they asked -- which is the plausible-number failure this server exists to refuse.
        await SeedBarAsync(Front, Thin(JuneStart));

        CapturingLogger<BarReselector> logger = new();
        CountingGateway gateway = Venue(FatLiquid(), ThinFront());

        BarReselectResult result = await Reselector(gateway, logger)
            .ReselectAsync(_mes, new BarRange(JuneStart, JuneStart.AddDays(1_000)), default);

        result.SeriesSkipped.Should().Be(1);
        result.BarsRevised.Should().Be(0);
        result.BarsRemoved.Should().Be(0);
        gateway.BarRequests.Should().Be(0, "the refusal comes before the venue is asked anything");

        logger.Messages.Should().Contain(
            message => message.Contains("250000", StringComparison.Ordinal)
                && message.Contains("5m", StringComparison.Ordinal),
            "a series skipped in silence is a window the operator believes was rewritten");
    }

    [Fact]
    public async Task CoverageRowsOverlappingTheWindow_AreDeleted()
    {
        // A coverage row is a claim that a contract answered a range with nothing, and a settled one never
        // expires. Every such claim touching a re-decided window was recorded against a decision that has
        // just been overturned, so leaving one standing would suppress the next read of the window on the
        // strength of a question asked under the old answer.
        //
        // OVERLAP, not containment, and the straddling row below is why: MemoiseEmpty deliberately cuts a
        // claim at the settled age and Union merges touching rows, so a claim that reaches into the window
        // from outside it is the ordinary shape rather than the exotic one. Losing a claim outside the
        // window costs one re-ask; keeping a permanent one inside it costs the window.
        await SeedWindowAsync(Front, Thin);
        await SeedCoverageAsync(Liquid, Market(2026, 6, 15, 12), Market(2026, 6, 15, 18));
        await SeedCoverageAsync(Front, JuneStart.AddMinutes(20), JuneStart.AddMinutes(30));
        await SeedCoverageAsync(Front, Market(2026, 6, 17, 9), Market(2026, 6, 17, 10));

        CountingGateway gateway = Venue(FatLiquid(), ThinFront());
        BarReselectResult result = await Reselector(gateway).ReselectAsync(_mes, Window, default);

        result.BarsRevised.Should().Be(
            12, "a live memo must not suppress the fetch the reselect exists to redo");

        List<BarCoverageRecord> claims = await _database.BarCoverage
            .AsNoTracking()
            .OrderBy(c => c.RangeStart)
            .ToListAsync();

        claims.Should().ContainSingle("only the claim that misses the window entirely survives");
        claims[0].RangeStart.Should().Be(Market(2026, 6, 17, 9));
    }

    [Fact]
    public async Task AWholeReadDegradation_ThrowsNamingTheReason()
    {
        // A read degrades to the venue's own pick and says so in a warning, because serving something is
        // better than serving nothing. A verb that REWRITES has the opposite duty: a plan built on a guess
        // would stamp that guess onto rows the store already held, so it refuses and the operator is told
        // which of the three conditions it was.
        await SeedWindowAsync(Front, Thin);

        CountingGateway gateway = Venue(FatLiquid(), ThinFront());
        gateway.ListsTheInstrument = false;

        Func<Task> reselect = () => Reselector(gateway).ReselectAsync(_mes, Window, default);

        await reselect.Should().ThrowAsync<InvalidOperationException>()
            .Where(exception => exception.Message.Contains("MES", StringComparison.Ordinal))
            .Where(exception => exception.Message.Contains("ProjectX__DataTier", StringComparison.Ordinal));

        List<BarRecord> stored = await StoredAsync(Window.Start, Window.End);
        stored.Should().OnlyContain(row => row.ContractId == Front, "a refusal writes nothing");
    }

    /// <summary>A market-time instant, on the hour.</summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <param name="hour">The hour, Central.</param>
    /// <returns>The instant, in UTC.</returns>
    private static DateTimeOffset Market(int year, int month, int day, int hour) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, 0)).ToUniversalTime();

    /// <summary>The thin series the venue's own pick answers a June window with.</summary>
    /// <param name="bucket">When the bucket opens.</param>
    /// <returns>The bar.</returns>
    private static Bar Thin(DateTimeOffset bucket) => new(bucket, 100m, 101m, 99m, 100.5m, 100);

    /// <summary>The series the contract that was actually trading answers with.</summary>
    /// <param name="bucket">When the bucket opens.</param>
    /// <returns>The bar.</returns>
    /// <remarks>
    /// A ten-point range against the thin series' two, so a projection over one is distinguishable from a
    /// projection over the other by its value rather than by its existence.
    /// </remarks>
    private static Bar Fat(DateTimeOffset bucket) => new(bucket, 200m, 210m, 190m, 205m, 5_000);

    /// <summary>The window's twelve buckets, each shaped by a builder.</summary>
    /// <param name="shape">What each bucket holds.</param>
    /// <returns>The bars, ascending.</returns>
    private static IEnumerable<Bar> Bars(Func<DateTimeOffset, Bar> shape) =>
        Bars(shape, JuneStart, 12);

    /// <summary>A run of five-minute buckets, each shaped by a builder.</summary>
    /// <param name="shape">What each bucket holds.</param>
    /// <param name="from">The first bucket.</param>
    /// <param name="count">How many buckets.</param>
    /// <returns>The bars, ascending.</returns>
    private static IEnumerable<Bar> Bars(Func<DateTimeOffset, Bar> shape, DateTimeOffset from, int count) =>
        Enumerable.Range(0, count).Select(i => shape(from.AddMinutes(5 * i)));

    /// <summary>Every five-minute bucket of the whole session, each shaped by a builder.</summary>
    /// <param name="shape">What each bucket holds.</param>
    /// <returns>The bars, ascending.</returns>
    private static IEnumerable<Bar> SessionBars(Func<DateTimeOffset, Bar> shape) =>
        Bars(shape, Session.Start, (int)((Session.End - Session.Start).TotalMinutes / 5));

    /// <summary>The winner's twelve buckets.</summary>
    /// <returns>The bars.</returns>
    private static IEnumerable<Bar> FatLiquid() => Bars(Fat);

    /// <summary>The venue front's twelve thin buckets.</summary>
    /// <returns>The bars.</returns>
    private static IEnumerable<Bar> ThinFront() => Bars(Thin);

    /// <summary>A venue listing both June candidates, with <see cref="Front"/> the active one.</summary>
    /// <param name="liquid">What <see cref="Liquid"/> answers.</param>
    /// <param name="front">What <see cref="Front"/> answers.</param>
    /// <returns>The double.</returns>
    private static CountingGateway Venue(IEnumerable<Bar> liquid, IEnumerable<Bar> front) =>
        new(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [Liquid] = liquid,
                [Front] = front,
            },
            Front);

    /// <summary>One stored row, at the fixture's venue and resolution.</summary>
    /// <param name="contractId">The contract the row is attributed to, or <see langword="null"/> for a row
    /// written before the provenance migration.</param>
    /// <param name="bar">The numbers the row holds.</param>
    /// <returns>The row.</returns>
    private static BarRecord Row(string? contractId, Bar bar) =>
        new()
        {
            Venue = "test",
            Instrument = _mes.Symbol,
            ResolutionMinutes = 5,
            BucketStart = bar.OpenTime,
            Open = bar.Open,
            High = bar.High,
            Low = bar.Low,
            Close = bar.Close,
            Volume = bar.Volume,
            ContractId = contractId,
            RecordedAt = JuneStart,
        };

    /// <summary>The indicator catalogue every projection here shares.</summary>
    /// <param name="calendar">The calendar the case is built on.</param>
    /// <returns>The catalogue.</returns>
    private static IndicatorCatalog Catalog(BarSessionCalendar? calendar = null) =>
        new(Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar ?? Calendar);

    /// <summary>The registry every service here shares.</summary>
    /// <returns>The registry.</returns>
    private static InstrumentRegistry Registry() =>
        new(Options.Create(new MarketDataOptions
        {
            Instruments = "MES",
            SessionCloseCentral = "16:00",
            MaxRows = 5_000,
        }));

    /// <summary>The stored rows of a window, read past the tracker.</summary>
    /// <param name="from">The window start.</param>
    /// <param name="to">The window end, exclusive.</param>
    /// <returns>The rows, ascending.</returns>
    /// <remarks>
    /// <b><c>AsNoTracking</c>, and it is not tidiness.</b> These rows are written by statements the change
    /// tracker never sees, so a tracked read is answered from the identity map with whatever this context
    /// seeded rather than with the row the statement wrote.
    /// </remarks>
    private async Task<List<BarRecord>> StoredAsync(DateTimeOffset from, DateTimeOffset to) =>
        await _database.Bars
            .AsNoTracking()
            .Where(b => b.ResolutionMinutes == 5 && b.BucketStart >= from && b.BucketStart < to)
            .OrderBy(b => b.BucketStart)
            .ToListAsync();

    /// <summary>Every ATR value standing over the window, ascending by bucket.</summary>
    /// <returns>The values.</returns>
    private async Task<List<decimal>> AtrValuesAsync() =>
        await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Indicator == "atr"
                && v.BucketStart >= Window.Start
                && v.BucketStart < Window.End)
            .OrderBy(v => v.BucketStart)
            .Select(v => v.Value)
            .ToListAsync();

    /// <summary>Every bucket an ATR value stands over inside the window, ascending.</summary>
    /// <returns>The bucket starts.</returns>
    private async Task<List<DateTimeOffset>> AtrBucketsAsync() =>
        await _database.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Indicator == "atr"
                && v.BucketStart >= Window.Start
                && v.BucketStart < Window.End)
            .OrderBy(v => v.BucketStart)
            .Select(v => v.BucketStart)
            .ToListAsync();

    /// <summary>Seeds a permanent "this contract answered nothing here" claim.</summary>
    /// <param name="contractId">The contract the claim is recorded under.</param>
    /// <param name="from">The claim's start.</param>
    /// <param name="to">The claim's end, exclusive.</param>
    private async Task SeedCoverageAsync(string contractId, DateTimeOffset from, DateTimeOffset to)
    {
        _database.BarCoverage.Add(new BarCoverageRecord
        {
            Venue = "test",
            Instrument = _mes.Symbol,
            ResolutionMinutes = 5,
            ContractId = contractId,
            RangeStart = from,
            RangeEnd = to,
            RecordedAt = JuneStart,
            ExpiresAt = null,
        });

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>Seeds a run of bars under one contract id, one statement at a time.</summary>
    /// <param name="contractId">The contract the rows are attributed to.</param>
    /// <param name="bars">The bars.</param>
    private async Task SeedBarsAsync(string? contractId, IEnumerable<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            await SeedBarAsync(contractId, bar);
        }
    }

    /// <summary>Seeds a long run of bars in one round trip.</summary>
    /// <param name="contractId">The contract the rows are attributed to.</param>
    /// <param name="bars">The bars.</param>
    /// <remarks>
    /// A whole session is 276 five-minute buckets, and seeding those one <c>SaveChanges</c> at a time is 276
    /// round trips against a container the whole collection shares. The tracker is still cleared afterwards,
    /// for the reason <see cref="SeedBarAsync"/> gives.
    /// </remarks>
    private async Task SeedManyAsync(string? contractId, IEnumerable<Bar> bars)
    {
        foreach (Bar bar in bars)
        {
            _database.Bars.Add(Row(contractId, bar));
        }

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>Seeds the whole window under one contract id.</summary>
    /// <param name="contractId">The contract the rows are attributed to.</param>
    /// <param name="shape">What each bucket holds.</param>
    private Task SeedWindowAsync(string contractId, Func<DateTimeOffset, Bar> shape) =>
        SeedBarsAsync(contractId, Bars(shape));

    /// <summary>Seeds one bucket under one contract id.</summary>
    /// <param name="contractId">The contract the row is attributed to.</param>
    /// <param name="bar">The numbers the row holds.</param>
    /// <remarks>
    /// <b>The tracker is cleared afterwards (gh#387).</b> This row goes in through the change tracker while
    /// the reads under test are ordinary queries; left tracked, the identity map would answer them with the
    /// instance this method wrote rather than with the row the store holds.
    /// </remarks>
    private async Task SeedBarAsync(string? contractId, Bar bar)
    {
        _database.Bars.Add(Row(contractId, bar));

        await _database.SaveChangesAsync();
        _database.ChangeTracker.Clear();
    }

    /// <summary>Projects the indicators over everything the store holds, as the store now stands.</summary>
    /// <remarks>
    /// Through <c>IndicatorRebuilder</c> rather than through the projector directly, because a projection
    /// pass refuses to run outside a transaction and <c>SeriesUnitOfWork</c> is internal to the host.
    /// </remarks>
    private async Task RebuildAsync()
    {
        IndicatorRebuilder rebuilder = new(
            _database,
            new IndicatorProjector(_database, Catalog(), NullLogger<IndicatorProjector>.Instance),
            Registry(),
            new FakeTimeProvider(Now),
            NullLogger<IndicatorRebuilder>.Instance);

        await rebuilder.RebuildAsync(_mes.Symbol, default);
        _database.ChangeTracker.Clear();
    }

    /// <summary>Builds the reselector around a venue double the case already holds.</summary>
    /// <param name="gateway">The venue double.</param>
    /// <param name="logger">A logger, when the case needs to read what the run said it did.</param>
    /// <param name="calendar">The session calendar, when the case declares a holiday.</param>
    /// <returns>The reselector.</returns>
    private BarReselector Reselector(
        CountingGateway gateway,
        ILogger<BarReselector>? logger = null,
        BarSessionCalendar? calendar = null)
    {
        FakeTimeProvider clock = new(Now);
        BarSessionCalendar sessions = calendar ?? Calendar;
        IndicatorProjector projector =
            new(_database, Catalog(sessions), NullLogger<IndicatorProjector>.Instance);

        BarCacheService cache = new(
            _database,
            gateway,
            sessions,
            projector,
            Registry(),
            new ContractDirectory(clock),
            clock,
            NullLogger<BarCacheService>.Instance);

        return new BarReselector(
            _database,
            cache,
            projector,
            sessions,
            clock,
            logger ?? NullLogger<BarReselector>.Instance);
    }
}
