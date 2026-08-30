using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The indicator projection writes its values through the store's own conflict resolution. These pin that a
/// pass which loses the race <b>updates</b> rather than faulting (gh#133).
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier, and only this tier.</b> The claim is about what one snapshot can see of another
/// transaction's committed rows, and the unit suite's in-memory provider has neither snapshots nor
/// transactions — nor an <c>ON CONFLICT</c> to leave the decision to. A test of it there would be green on the
/// day the fix was reverted.
/// </para>
/// <para>
/// <b>Why it is a defect and not merely a race.</b> A projection pass recomputes the <i>whole series its
/// snapshot can see</i> — that is <see href="../documentation/adr/0006-indicators-as-projections.md">ADR-0006</see>'s
/// reproducibility requirement rather than an accident — so two fills of ranges that share no bucket, no bar
/// and no coverage row still both produce values for the history sitting in front of both. Deciding
/// insert-versus-update from a dictionary read out of this transaction's snapshot means the loser
/// <c>INSERT</c>s over rows that already exist, aborts the whole unit of work on <c>23505</c>, and the caller
/// of <c>get_bars</c> is handed a database error for an ordinary question.
/// </para>
/// <para>
/// Each test owns a private venue id, so the series are disjoint inside the shared container — see
/// <see cref="ConcurrencyHarness"/>.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class IndicatorValueUpsertConcurrencyTests(SchemaFixture fixture)
{
    /// <summary>The bucket whose bar is restated, so the values over it have to be rewritten.</summary>
    private const int RestatedBucket = 15;

    /// <summary>How far the restated bar moves. Far above the stored scale, so it is unambiguously a change.</summary>
    private const decimal Restatement = 11.75m;

    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    // ── The reported defect ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoFillsOfDisjointRangesOverAnUnprojectedSeries_BothLand_AndConvergeOnTheUncontendedNumbers()
    {
        // THE regression. Two fills of DISJOINT ranges -- 20..25 and 25..30 -- so neither writes a bar the
        // other writes, neither writes a coverage row (the venue answers both with bars), and the reconcile
        // has nothing to delete. They collide anyway, and only on the projection: a pass recomputes the whole
        // series its snapshot can see, so both produce values over the history in front of both ranges.
        //
        // THE SEEDED SERIES HAS NO VALUES OVER IT, and that is an ordinary state rather than a contrivance --
        // IndicatorOptions.AtrPeriod moving from 14 to 3 leaves every bar without a row under the new key,
        // because (Indicator, Period) IS the storage key.
        //
        // The interleaving is placed AFTER the bar write's overlap read, which is the fill transaction's
        // first statement and so where its snapshot is taken. `"Volume"` is the discriminator: the read that
        // opens GetBarsAsync projects to the bucket alone and never names it.
        string venue = ConcurrencyHarness.Venue();
        await SeedBarsThatNothingHasProjectedOverAsync(venue);

        await using TopstepXDbContext winnerStore = _fixture.CreateContext();

        async Task TheOtherFillProjectsOverTheSameHistory()
        {
            BarCacheService winner =
                ConcurrencyHarness.Cache(winnerStore, venue, ConcurrencyHarness.Bars(20, 25), Now);
            await winner.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(20, 25),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"Volume\"", venue, TheOtherFillProjectsOverTheSameHistory);

        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext loserStore = _fixture.CreateContext(straddle);
        BarCacheService loser =
            ConcurrencyHarness.Cache(loserStore, venue, ConcurrencyHarness.Bars(25, 30), Now, log);

        Func<Task> read = () => loser.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(25, 30),
            CancellationToken.None);

        await read.Should().NotThrowAsync(
            "the caller asked an ordinary question about a range no other fill was writing, and the rows it "
            + "collided on are in the store because the other fill put them there");

        straddle.Fired.Should().BeTrue(
            "the collision is the test -- if the other fill never ran inside this one's transaction, this "
            + "passed by exercising nothing");
        log.Messages.Should().ContainMatch(
            "*serialization failure*",
            "the losing pass has to have MET the other's rows, not merely followed them. A retry leaves no "
            + "other trace, and asserting only that the call succeeded would pass just as well on a run "
            + "where nothing collided at all");

        // The two racing fills must land on exactly what ONE uncontended fill of the union produces. That is
        // ADR-0006 stated as an assertion: the stored series is a function of the bars, not of how many
        // passes happened to write it or in what order.
        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> raced =
            await ValuesAsync(venue);
        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> uncontended =
            await UncontendedProjectionOfAsync(ConcurrencyHarness.Bars(0, 30));

        raced.Should().BeEquivalentTo(
            uncontended,
            "every (Indicator, Period, BucketStart) the bars justify exists exactly once and holds the "
            + "number a single pass over the same bars produces -- a losing INSERT that was swallowed would "
            + "leave the winner's older numbers standing, which is quieter than the duplicate key it replaces "
            + "and worse");
    }

    // ── The two clauses the statement's SET carries ──────────────────────────────────────────────────

    [Fact]
    public async Task ARecomputedValueThatMoved_IsWrittenOverTheStoredOne()
    {
        // The revision half of the write, with no race in it at all. A bar is restated after its values were
        // stored, so the pass recomputes numbers that differ from the ones standing -- and the row has to end
        // up holding the new number. An upsert that only ever inserted would leave the stale value in place
        // and report success.
        string venue = ConcurrencyHarness.Venue();
        await SeedAsync(venue, ConcurrencyHarness.Bars(0, 20));

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> before =
            await ValuesAsync(venue);

        await RestateOneBarAsync(venue);
        await ProjectInOneSnapshotAsync(venue, Now.AddMinutes(1));

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> after =
            await ValuesAsync(venue);

        (string, int, DateTimeOffset) revised = ("rsi", 3, ConcurrencyHarness.Bucket(RestatedBucket));

        after[revised].Should().NotBe(
            before[revised],
            "the bar under that bucket moved by {0}, so the value over it is a different number and the "
            + "stored row has to say so",
            Restatement);

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> uncontended =
            await UncontendedProjectionOfAsync(RestatedSeries());

        after.Should().BeEquivalentTo(
            uncontended,
            "a replay over the restated bars is the definition of what the store should hold (ADR-0006), so "
            + "asserting only that the number CHANGED would ratify any wrong number that differs");
    }

    [Fact]
    public async Task ARevisedValue_CarriesTheClockOfThePassThatRevisedIt()
    {
        // RecordedAt is the store's answer to "when was this number last actually different", and the data
        // dictionary says it is bumped only when Value changes. Both halves are here in one pass: the bucket
        // whose bar was restated moves, and a bucket in front of it -- whose value the restatement cannot
        // reach, because every indicator here is causal -- does not.
        string venue = ConcurrencyHarness.Venue();
        DateTimeOffset later = Now.AddMinutes(1);

        await SeedAsync(venue, ConcurrencyHarness.Bars(0, 20));
        await RestateOneBarAsync(venue);
        await ProjectInOneSnapshotAsync(venue, later);

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), DateTimeOffset> stamps =
            await RecordedAtAsync(venue);

        stamps[("rsi", 3, ConcurrencyHarness.Bucket(RestatedBucket))].Should().Be(
            later,
            "the value moved, so the row records when it moved -- a revision that kept the first pass's "
            + "clock would report the series as settled since a time it demonstrably was not");

        stamps[("rsi", 3, ConcurrencyHarness.Bucket(RestatedBucket - 5))].Should().Be(
            Now,
            "no bar at or before that bucket moved, so its value is unchanged and its timestamp must not "
            + "drift -- a rewrite with identical numbers is indistinguishable from a real revision once the "
            + "clock has moved");
    }

    // ── The awkward correct input ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConfirmingPass_WritesNothingAndMovesNoTimestamp()
    {
        // The green case a write change is most likely to break, and the reason the skip-unchanged rule is
        // still decided in C#: a pass that recomputes the same numbers must reconcile to an EMPTY DIFF
        // (R-2.8, ADR-0006). A statement sent for every value in the series would be correct row-by-row and
        // would still move every RecordedAt in it, which is how the gh#37 rounding defect stayed invisible
        // for a whole phase.
        string venue = ConcurrencyHarness.Venue();
        await SeedAsync(venue, ConcurrencyHarness.Bars(0, 20));

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), DateTimeOffset> before =
            await RecordedAtAsync(venue);

        int changed = await ProjectInOneSnapshotAsync(venue, Now.AddMinutes(1));

        changed.Should().Be(
            0,
            "nothing about the bars changed, so a confirming pass has nothing to write and nothing to remove");

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), DateTimeOffset> after =
            await RecordedAtAsync(venue);

        after.Should().BeEquivalentTo(before, "an empty diff means the rows were not touched, not that they "
            + "were rewritten with the same numbers");
    }

    // ── The invariant the raw statement newly depends on ─────────────────────────────────────────────

    [Fact]
    public async Task AProjectionOutsideAUnitOfWork_RefusesRatherThanWritingValuesOnTheirOwn()
    {
        // The write is one statement the store executes when it is sent, not an entity the caller's
        // SaveChanges emits later. Inside the caller's transaction that is invisible -- it commits with
        // everything else. OUTSIDE one it autocommits on its own, and the reconcile's deletes, which are
        // still tracked, do not: the pass would leave values committed that the same pass had already decided
        // to remove, and bars committed without them, which is precisely the partial commit ProjectAsync's own
        // contract forbids.
        //
        // It cannot fire as shipped -- both call sites go through SeriesUnitOfWork -- and that is the point,
        // exactly as it is for the whole-series guard next to it. It fires when someone adds a third call
        // site, which is the only way this can be got wrong.
        string venue = ConcurrencyHarness.Venue();
        await SeedBarsThatNothingHasProjectedOverAsync(venue);

        await using TopstepXDbContext store = _fixture.CreateContext();

        Func<Task> project = () => ConcurrencyHarness.Projector(store).ProjectAsync(
            venue,
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            Now,
            CancellationToken.None);

        (await project.Should().ThrowAsync<InvalidOperationException>(
            "a projection that is not inside a unit of work cannot keep its own promise that bars and the "
            + "values derived from them commit together"))
            .WithMessage("*transaction*");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        (await ConcurrencyHarness.BucketsWithValuesAsync(reader, venue)).Should().BeEmpty(
            "refusing means nothing was written, not that the write happened and then complained");
    }

    // ── Setup and oracles ────────────────────────────────────────────────────────────────────────────

    /// <summary>Fills a series through the ordinary cache-aside path.</summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <param name="bars">The bars the venue will serve.</param>
    private async Task SeedAsync(string venue, IReadOnlyList<Bar> bars)
    {
        await using TopstepXDbContext seed = _fixture.CreateContext();

        BarCacheService cache = ConcurrencyHarness.Cache(seed, venue, bars, Now);
        await cache.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            new BarRange(bars[0].OpenTime, bars[^1].OpenTime.AddMinutes(ConcurrencyHarness.ResolutionMinutes)),
            CancellationToken.None);
    }

    /// <summary>
    /// Fills a series and then removes the values standing over it.
    /// </summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <remarks>
    /// The removal is what gives two otherwise-disjoint fills a shared write set: each projects the whole
    /// series it can see, so both produce the keys over this history. Leaving the values in place would let
    /// both passes recognise them as already produced, and nothing would be inserted twice.
    /// </remarks>
    private async Task SeedBarsThatNothingHasProjectedOverAsync(string venue)
    {
        await using TopstepXDbContext seed = _fixture.CreateContext();

        BarCacheService cache = ConcurrencyHarness.Cache(seed, venue, ConcurrencyHarness.Bars(0, 20), Now);
        await cache.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 20),
            CancellationToken.None);

        List<IndicatorValueRecord> values = await seed.IndicatorValues
            .Where(v => v.Venue == venue)
            .ToListAsync();

        seed.IndicatorValues.RemoveRange(values);
        await seed.SaveChangesAsync();
    }

    /// <summary>The seeded series with one bar restated, which is what the venue would answer with.</summary>
    /// <returns>The bars.</returns>
    private static IReadOnlyList<Bar> RestatedSeries() =>
    [
        .. ConcurrencyHarness.Bars(0, 20).Select(bar => bar.OpenTime == ConcurrencyHarness.Bucket(RestatedBucket)
            ? bar with
            {
                Open = bar.Open + Restatement,
                High = bar.High + Restatement,
                Low = bar.Low + Restatement,
                Close = bar.Close + Restatement,
            }
            : bar),
    ];

    /// <summary>
    /// Moves one stored bar, so the values over it stop matching the bars.
    /// </summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <remarks>
    /// Written straight to the store rather than re-fetched, because a re-fetch of a range already covered
    /// never reaches the venue — <c>BarGapDetector</c> finds no hole — and would exercise nothing. A venue
    /// restating settled history produces exactly this state.
    /// </remarks>
    private async Task RestateOneBarAsync(string venue)
    {
        await using TopstepXDbContext store = _fixture.CreateContext();

        BarRecord bar = await store.Bars.SingleAsync(b =>
            b.Venue == venue
            && b.Instrument == ConcurrencyHarness.Symbol
            && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes
            && b.BucketStart == ConcurrencyHarness.Bucket(RestatedBucket));

        bar.Open += Restatement;
        bar.High += Restatement;
        bar.Low += Restatement;
        bar.Close += Restatement;

        await store.SaveChangesAsync();
    }

    /// <summary>
    /// Runs one projection pass over a series inside a single snapshot, and commits it.
    /// </summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <param name="now">The clock the pass stamps on rows it changes.</param>
    /// <returns>How many rows the pass changed.</returns>
    private async Task<int> ProjectInOneSnapshotAsync(string venue, DateTimeOffset now)
    {
        // RepeatableRead, which is what both production call sites read a series at (SeriesUnitOfWork).
        await using TopstepXDbContext store = _fixture.CreateContext();
        await using IDbContextTransaction transaction = await store.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await ConcurrencyHarness.Projector(store).ProjectAsync(
            venue,
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            now,
            CancellationToken.None);

        await store.SaveChangesAsync();
        await transaction.CommitAsync();

        return changed;
    }

    /// <summary>
    /// What one uncontended fill of a bar set produces, as the oracle for what a raced one must equal.
    /// </summary>
    /// <param name="bars">The bars the venue serves.</param>
    /// <returns>The stored values, keyed.</returns>
    /// <remarks>
    /// Derived by running the projection over its own private venue rather than by restating the arithmetic
    /// here. The claim under test is <b>not</b> what an RSI is — that is pinned by hand-checked fixtures in
    /// the unit tier — it is that concurrency does not change the answer, and the only honest statement of
    /// that is "the same as without it".
    /// </remarks>
    private async Task<IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal>>
        UncontendedProjectionOfAsync(IReadOnlyList<Bar> bars)
    {
        string venue = ConcurrencyHarness.Venue();
        await SeedAsync(venue, bars);
        return await ValuesAsync(venue);
    }

    private async Task<IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal>>
        ValuesAsync(string venue)
    {
        await using TopstepXDbContext reader = _fixture.CreateContext();

        return await reader.IndicatorValues
            .Where(v => v.Venue == venue
                && v.Instrument == ConcurrencyHarness.Symbol
                && v.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .ToDictionaryAsync(v => (v.Indicator, v.Period, v.BucketStart), v => v.Value);
    }

    private async Task<IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), DateTimeOffset>>
        RecordedAtAsync(string venue)
    {
        await using TopstepXDbContext reader = _fixture.CreateContext();

        return await reader.IndicatorValues
            .Where(v => v.Venue == venue
                && v.Instrument == ConcurrencyHarness.Symbol
                && v.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .ToDictionaryAsync(v => (v.Indicator, v.Period, v.BucketStart), v => v.RecordedAt);
    }
}
