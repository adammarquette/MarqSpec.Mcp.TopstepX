using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// `RepeatableRead` turns a silent last-writer-wins into a `40001`. These pin what happens next.
/// </summary>
/// <remarks>
/// <para>
/// <b>The conflict is not exotic, and the reason is the reconcile's own scope.</b> A pass sweeps the whole
/// series, so a whole-series sweep is a whole-series <i>write set</i> — two fills of ranges that share no
/// bucket, no bar and no coverage row still delete the <b>same</b> unjustified values, and the loser is
/// aborted with <c>could not serialize access due to concurrent delete</c>. Reasoning about the ranges a fill
/// <i>fetched</i> says nothing about the rows it <i>writes</i>.
/// </para>
/// <para>
/// <b>And the retry converges rather than gambling.</b> In every shape of this conflict the transaction that
/// won committed exactly the work the loser was missing, so the second attempt runs over a strictly
/// better-informed store: the rows are already deleted, or the coverage row already answers the range. That is
/// what makes one bounded retry the right size — not optimism, but the fact that a second collision would mean
/// sustained contention rather than a race.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class SerializationFailureTests(SchemaFixture fixture)
{
    private readonly SchemaFixture _fixture = fixture;

    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(120);

    [Fact]
    public async Task TwoFillsDeletingTheSameUnjustifiedValues_BothSucceed()
    {
        // Fills of DISJOINT ranges -- 40..50 and 50..60 -- over a series that holds values nothing justifies.
        // Neither writes a bar, a coverage row or an indicator bucket the other writes. They collide anyway,
        // on the reconcile's delete, which is exactly the unscoped sweep this PR documents.
        string venue = ConcurrencyHarness.Venue();
        await SeedSeriesWithAnUnjustifiedTailAsync(venue);

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task FillTheOtherRangeAndCommit()
        {
            BarCacheService other =
                ConcurrencyHarness.Cache(otherStore, venue, ConcurrencyHarness.Bars(40, 50), Now);
            await other.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(40, 50),
                CancellationToken.None);
        }

        // Placed before the value read, so the losing pass has already taken its snapshot and still believes
        // the tail is there to delete.
        InterleavingInterceptor straddle = InterleavingInterceptor.Before(
            "FROM \"IndicatorValues\"", venue, FillTheOtherRangeAndCommit);

        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService fill =
            ConcurrencyHarness.Cache(store, venue, ConcurrencyHarness.Bars(50, 60), Now, log);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(50, 60),
            CancellationToken.None);

        await read.Should().NotThrowAsync(
            "a serialization failure between two fills is a transient condition Postgres documents as "
            + "retryable, and the transaction that won committed exactly the work this one was missing");

        straddle.Fired.Should().BeTrue("the conflicting transaction has to actually run");
        log.Messages.Should().ContainMatch(
            "*serialization failure*",
            "a retry leaves no other trace -- asserting only on success would pass against a run where "
            + "nothing collided at all");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        IReadOnlyList<DateTimeOffset> surviving =
            await ConcurrencyHarness.BucketsWithValuesAsync(reader, venue);

        surviving.Should().NotContain(
            ConcurrencyHarness.Bucket(35), "the tail both passes agreed was unjustified is gone");
        surviving.Should().Contain(
            ConcurrencyHarness.Bucket(55), "and the retrying pass still landed its own range");
    }

    [Fact]
    public async Task TwoFillsRefreshingTheSameEmptyRange_BothSucceed_WithNoBarsInvolved()
    {
        // The other branch, and the ordinary one: two agents polling the same instrument. Nothing is fetched
        // and no bar is written -- the collision is on the coverage ledger's REFRESH of an existing row,
        // which `RecordEmptyAsync` does rather than only inserting. A dichotomy over bar ranges does not
        // reach this at all.
        string venue = ConcurrencyHarness.Venue();

        // A first pass over a range the venue answers empty, near enough to the present that the row expires.
        DateTimeOffset early = ConcurrencyHarness.Bucket(80);
        await using (TopstepXDbContext seed = _fixture.CreateContext())
        {
            BarCacheService cold = ConcurrencyHarness.Cache(seed, venue, [], early);
            await cold.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(60, 70),
                CancellationToken.None);
        }

        // Past the TTL, so the row no longer covers the range and both passes ask again -- and both refresh
        // the same row.
        DateTimeOffset later = early + BarCacheService.RecentEmptyTtl + TimeSpan.FromMinutes(1);

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task AskTheSameRangeAndCommit()
        {
            BarCacheService other = ConcurrencyHarness.Cache(otherStore, venue, [], later);
            await other.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(60, 70),
                CancellationToken.None);
        }

        // AFTER, and matched on the ledger REFRESH's own predicate. `ExcludeCoveredAsync` also reads
        // BarCoverage, but it runs BEFORE the transaction opens -- firing there would let the other pass
        // commit before this one has a snapshot, and the conflict would never arise. `"RangeStart" =` appears
        // only in `RecordEmptyAsync`'s lookup, which is this transaction's first statement.
        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"RangeStart\" = ", venue, AskTheSameRangeAndCommit);

        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService fill = ConcurrencyHarness.Cache(store, venue, [], later, log);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(60, 70),
            CancellationToken.None);

        await read.Should().NotThrowAsync("two agents polling one instrument is the ordinary case, not a fault");
        straddle.Fired.Should().BeTrue();

        await using TopstepXDbContext reader = _fixture.CreateContext();
        int coverage = await reader.BarCoverage.CountAsync(c => c.Venue == venue);
        coverage.Should().Be(1, "the ledger tracks the latest answer for a range, not a history of asking");
    }

    [Fact]
    public async Task AConflictThatSurvivesTheRetry_SaysWhatHappened()
    {
        // Sustained contention rather than a race. One retry is the whole budget, and past it the call has to
        // fail -- but as a named condition a caller can act on, not as the stack-shaped fault the raw
        // PostgresException reaches the tool surface as (gh#69's shape).
        string venue = ConcurrencyHarness.Venue();
        await SeedSeriesWithAnUnjustifiedTailAsync(venue);

        // TWO rows nothing justifies, and one is taken per round. Each attempt's snapshot therefore still
        // holds a row that a concurrent transaction deletes before this one commits -- which is what makes
        // this a test of EXHAUSTION rather than of the first attempt. One stray would let the retry succeed.
        await using (TopstepXDbContext strays = _fixture.CreateContext())
        {
            foreach (int bucket in (int[])[90, 91])
            {
                strays.IndicatorValues.Add(new IndicatorValueRecord
                {
                    Venue = venue,
                    Instrument = ConcurrencyHarness.Symbol,
                    ResolutionMinutes = ConcurrencyHarness.ResolutionMinutes,
                    Indicator = "atr",
                    Period = 3,
                    BucketStart = ConcurrencyHarness.Bucket(bucket),
                    Value = 1.5m,
                    RecordedAt = Now,
                });
            }

            await strays.SaveChangesAsync();
        }

        int round = 0;

        async Task DeleteOneStrayFromAnotherConnectionAsync()
        {
            DateTimeOffset bucket = ConcurrencyHarness.Bucket(90 + round);
            round++;

            await using TopstepXDbContext other = _fixture.CreateContext();

            List<IndicatorValueRecord> doomed = await other.IndicatorValues
                .Where(v => v.Venue == venue && v.BucketStart == bucket)
                .ToListAsync();

            other.IndicatorValues.RemoveRange(doomed);
            await other.SaveChangesAsync();
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.Before(
            "FROM \"IndicatorValues\"", venue, DeleteOneStrayFromAnotherConnectionAsync, times: 2);

        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService pass =
            ConcurrencyHarness.Cache(store, venue, ConcurrencyHarness.Bars(50, 60), Now);

        Func<Task> read = () => pass.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(50, 60),
            CancellationToken.None);

        Exception? thrown = await Record.ExceptionAsync(read);

        straddle.Firings.Should().Be(2, "the retry has to have met the same conflict the first attempt did");
        thrown.Should().BeOfType<StoreContentionException>(
            "past one retry this is contention, not a race, and looping would hide it");
        thrown!.Message.Should().Match("*concurrent*").And.Match("*retry*");
    }

    /// <summary>
    /// Fills forty buckets, then deletes the last ten bars, so the values over them are unjustified.
    /// </summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <remarks>
    /// This is what gives two otherwise-disjoint fills a shared write set: both reconcile the whole series, so
    /// both delete this same tail. It is a real state — the first pass over a series rolled before ADR-0011,
    /// or a venue restatement that moves a contract boundary, reaches it without anyone deleting a bar by
    /// hand.
    /// </remarks>
    private async Task SeedSeriesWithAnUnjustifiedTailAsync(string venue)
    {
        await using TopstepXDbContext seed = _fixture.CreateContext();

        BarCacheService cache = ConcurrencyHarness.Cache(seed, venue, ConcurrencyHarness.Bars(0, 40), Now);
        await cache.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 40),
            CancellationToken.None);

        List<BarRecord> tail = await seed.Bars
            .Where(b => b.Venue == venue
                && b.Instrument == ConcurrencyHarness.Symbol
                && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes
                && b.BucketStart >= ConcurrencyHarness.Bucket(30))
            .ToListAsync();

        seed.Bars.RemoveRange(tail);
        await seed.SaveChangesAsync();
    }
}
