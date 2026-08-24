using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// `R-1.7` says a range the vendor answers <b>empty</b> is recorded as covered. These pin that it still is
/// when the caller recording it loses a race to another caller recording the same range (gh#122).
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier, and only this tier.</b> The claim is about what one snapshot can see of another
/// transaction's committed rows, and about a statement — <c>ON CONFLICT … DO UPDATE</c> — the unit suite's
/// in-memory provider does not have. A test there would be green on the day the fix was reverted.
/// </para>
/// <para>
/// <b>Why it is a defect and not merely a race.</b> A losing insert on the ledger's composite key does not
/// lose a row: it aborts the whole transaction, so the bars, the coverage ledger and the projection over the
/// same series all roll back, and the caller of <c>get_bars</c> is handed a database error for asking about a
/// quiet range. The row it collided on is one the other caller committed — so the store is fine, and only the
/// answer is lost. Two agents polling one instrument is the ordinary case, not an exotic one.
/// </para>
/// <para>
/// <b>Why a bar slice rides along, when the defect needs no bars at all.</b> The fix removes the ledger's
/// pre-read, so after it the coverage write is the <i>only</i> statement a purely-empty fill runs inside its
/// transaction — and a transaction whose first statement is the <c>INSERT</c> takes its snapshot at that
/// statement, which is too late for the interleaving to be placed. So these drive the case where the same
/// fill answers one range with bars and another empty: the bar write's overlap read is where the snapshot is
/// taken, and the ledger write that follows it in the same transaction is the one holding a stale view. That
/// is the same fill shape <c>BarGapDetector</c> produces whenever a window has a hole in it — one stored
/// bucket splits it into two ranges — and it is what makes the collision <i>placeable</i> rather than a race
/// the suite occasionally wins.
/// </para>
/// <para>
/// Each test owns a private venue id, so the series are disjoint inside the shared container — see
/// <see cref="ConcurrencyHarness"/>.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class CoverageLedgerConcurrencyTests(SchemaFixture fixture)
{
    private readonly SchemaFixture _fixture = fixture;

    /// <summary>A clock at which the empty range is <b>near the present</b>, so the claim expires.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    /// <summary>
    /// A clock at which the empty range is <b>settled history</b>, so the claim is permanent.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="BarCacheService.SettledHistoryAge"/> rather than written as a literal: the
    /// classification is <c>range.End &lt;= now - SettledHistoryAge</c>, and a hard-coded date would stop
    /// meaning "settled" the moment that policy was tuned — the test would then pass while asserting the
    /// wrong half of the asymmetry.
    /// </remarks>
    private static DateTimeOffset LongAfterTheRangeSettled =>
        ConcurrencyHarness.Bucket(20) + BarCacheService.SettledHistoryAge + TimeSpan.FromHours(1);

    /// <summary>The empty range both callers record — the second run of a window with a hole in it.</summary>
    private static BarRange TheEmptyRange =>
        new(ConcurrencyHarness.Bucket(11), ConcurrencyHarness.Bucket(20));

    [Fact]
    public async Task TwoCallersRecordingOneEmptyRange_BothSucceed_AndTheLedgerHoldsTheLaterAnswer()
    {
        // THE regression. Near the present, so the claim carries a TTL rather than being permanent.
        //
        // The two callers run at clocks one minute apart, because RecordedAt and ExpiresAt are the only
        // things that say which caller's answer the surviving row holds. A test that only counted rows would
        // pass with the losing write swallowed, which is worse than the duplicate key it replaces because it
        // is quiet: the ledger would then hold a claim nobody made, expiring at a moment nobody chose.
        string venue = ConcurrencyHarness.Venue();
        DateTimeOffset later = Now.AddMinutes(1);
        await SeedTheBucketThatSplitsTheWindowAsync(venue, Now);

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task TheOtherCallerRecordsTheSameRangeAndCommits()
        {
            BarCacheService other = ConcurrencyHarness.Cache(otherStore, venue, [], Now);
            await other.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(11, 20),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = Straddle(venue, TheOtherCallerRecordsTheSameRangeAndCommits);
        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService fill =
            ConcurrencyHarness.Cache(store, venue, ConcurrencyHarness.Bars(0, 10), later, log);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 20),
            CancellationToken.None);

        await read.Should().NotThrowAsync(
            "the ledger tracks the latest answer for a range rather than a history of asking, so a second "
            + "caller recording the same empty range is an update -- and two agents polling one instrument "
            + "is the case the ledger exists to make cheap, not a fault to hand back out of get_bars");

        AssertTheCallersActuallyCollided(straddle, log);

        BarCoverageRecord row = await TheOnlyCoverageRowAsync(venue);

        row.RangeStart.Should().Be(TheEmptyRange.Start);
        row.RangeEnd.Should().Be(TheEmptyRange.End);

        row.RecordedAt.Should().Be(
            later,
            "the surviving row carries the LOSING caller's clock, which is how a row it updated is told from "
            + "a row its write was silently dropped over");

        row.ExpiresAt.Should().Be(
            later + BarCacheService.RecentEmptyTtl,
            "the range sits near the present, where an empty answer means \"not yet\" -- believing it "
            + "permanently would blind the cache to the bar about to print, and the TTL has to be the one "
            + "the caller that actually wrote the row computed");

        await AssertTheFillStillDidItsWorkAsync(venue);
    }

    [Fact]
    public async Task TheSameRaceOverSettledHistory_LeavesAPermanentClaim()
    {
        // The other half of the asymmetry, under the identical collision. For settled history an empty answer
        // means "never", so ExpiresAt is null and re-asking is not merely wasteful, it is a venue request on
        // every single call forever.
        //
        // Both halves have to be driven, and driven through the SAME write: one statement writing ExpiresAt
        // unconditionally is correct for both, and one that hard-wired either answer would pass the other
        // test on its own.
        string venue = ConcurrencyHarness.Venue();
        DateTimeOffset settled = LongAfterTheRangeSettled;
        DateTimeOffset later = settled.AddMinutes(1);
        await SeedTheBucketThatSplitsTheWindowAsync(venue, settled);

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task TheOtherCallerRecordsTheSameRangeAndCommits()
        {
            BarCacheService other = ConcurrencyHarness.Cache(otherStore, venue, [], settled);
            await other.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(11, 20),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = Straddle(venue, TheOtherCallerRecordsTheSameRangeAndCommits);
        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService fill =
            ConcurrencyHarness.Cache(store, venue, ConcurrencyHarness.Bars(0, 10), later, log);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 20),
            CancellationToken.None);

        await read.Should().NotThrowAsync();
        AssertTheCallersActuallyCollided(straddle, log);

        BarCoverageRecord row = await TheOnlyCoverageRowAsync(venue);

        row.RecordedAt.Should().Be(later, "the losing caller's write is the one that stands");
        row.ExpiresAt.Should().BeNull(
            "a hole in settled history is not going to fill in, so the claim is permanent -- and null here "
            + "means NEVER rather than \"not recorded\", which is why it has to be written rather than left "
            + "to whatever the other caller happened to put there");

        await AssertTheFillStillDidItsWorkAsync(venue);
    }

    [Fact]
    public async Task AnExpiredClaimOverARangeThatHasSinceSettled_IsRefreshedAsPermanent()
    {
        // NOT a race, and green before the fix as well as after it -- this is the awkward CORRECT input the
        // Coding contract asks a new gate to be run against, and the behaviour gh#122 must not trade away
        // while moving the write.
        //
        // An expired row for the exact range is filtered out of the covered set, so it is invisible to the
        // caller -- but it is still in the table. The write therefore has to refresh it, and refresh it to
        // the classification that is true NOW: the range was near the present when it was first asked about
        // and carried a TTL; days later the same range is settled history and the claim becomes permanent.
        // A write that treated the stored ExpiresAt as a floor -- COALESCE over it, or omitting it from the
        // SET so the old value stands -- would leave a permanent claim wearing an expiry, and the range would
        // be re-fetched forever. Only a case where the old value is non-null and the new one is null can tell
        // those apart, and this is that case.
        string venue = ConcurrencyHarness.Venue();
        DateTimeOffset early = ConcurrencyHarness.Bucket(20) + TimeSpan.FromHours(1);

        await using (TopstepXDbContext seed = _fixture.CreateContext())
        {
            BarCacheService first = ConcurrencyHarness.Cache(seed, venue, [], early);
            await first.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, 20),
                CancellationToken.None);
        }

        (await TheOnlyCoverageRowAsync(venue)).ExpiresAt.Should().Be(
            early + BarCacheService.RecentEmptyTtl,
            "the first ask has to leave an EXPIRING row, or the refresh below is not a refresh over an "
            + "expired one and this test proves nothing");

        // Past the TTL and past the settling age, so the row no longer covers the range and the answer to
        // "when does this stop being believed" has changed since it was written.
        DateTimeOffset later = early + BarCacheService.SettledHistoryAge + TimeSpan.FromDays(1);

        await using TopstepXDbContext store = _fixture.CreateContext();
        BarCacheService again = ConcurrencyHarness.Cache(store, venue, [], later);

        BarReadResult result = await again.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 20),
            CancellationToken.None);

        result.VenueRequests.Should().BeGreaterThan(
            0,
            "an expired claim is invisible to the covered set, so the range is asked about again -- if it "
            + "were not, no second write happens and the assertions below would be reading the first row");

        BarCoverageRecord row = await TheOnlyCoverageRowAsync(venue);

        row.RecordedAt.Should().Be(later, "the ledger holds the latest answer, not a history of asking");
        row.ExpiresAt.Should().BeNull(
            "the range has settled since it was first asked about, so the claim over it is now permanent -- "
            + "a stored expiry must not survive as a floor under the classification");
    }

    /// <summary>
    /// Places the other caller's whole call between this one's snapshot and its ledger write.
    /// </summary>
    /// <param name="venue">The private venue id, which is how the right series is recognised.</param>
    /// <param name="otherCaller">The other caller, run to completion and committed.</param>
    /// <returns>The interceptor. Hand it to the context, and assert it fired.</returns>
    /// <remarks>
    /// <b>AFTER the bar write's overlap read</b>, which is the fill transaction's first statement and so
    /// where its snapshot is taken. Firing on anything <c>GetBarsAsync</c> runs before the transaction opens
    /// — the stored-bucket read, or <c>ExcludeCoveredAsync</c>'s own read of the ledger — would let the other
    /// caller commit before this one has a snapshot at all, and the coverage row would simply be visible: the
    /// interleaving would run and prove nothing.
    /// <para>
    /// <c>"Volume"</c> is the discriminator for the same reason it is in
    /// <c>BarUpsertConcurrencyTests</c>: the read that opens <c>GetBarsAsync</c> projects to the bucket alone
    /// and the coverage read is a different table, so the first <c>SELECT</c> naming a bar's volume is the
    /// overlap read.
    /// </para>
    /// </remarks>
    private static InterleavingInterceptor Straddle(string venue, Func<Task> otherCaller) =>
        InterleavingInterceptor.After("\"Volume\"", venue, otherCaller);

    /// <summary>
    /// Asserts the two callers met, rather than the test having quietly run them one after the other.
    /// </summary>
    /// <param name="straddle">The interceptor that placed the other caller.</param>
    /// <param name="log">What the fill said it did.</param>
    /// <remarks>
    /// Two separate claims, and both are needed. The interceptor firing says the other call ran at the chosen
    /// point; the retry says this one's ledger write actually <b>collided</b> with it. Postgres refuses a
    /// conflicting <c>ON CONFLICT DO UPDATE</c> against a row committed after the snapshot with a
    /// <c>40001</c>, which `R-2.10` retries once — and a retry leaves no other trace, so asserting on the
    /// outcome alone would pass just as well against a run where nothing collided.
    /// </remarks>
    private static void AssertTheCallersActuallyCollided(
        InterleavingInterceptor straddle,
        CapturingLogger<BarCacheService> log)
    {
        straddle.Fired.Should().BeTrue(
            "the interleaving is the test -- if the other caller never ran between the snapshot and the "
            + "ledger write, this passed by not exercising anything");

        log.Messages.Should().ContainMatch(
            "*serialization failure*",
            "the ledger write has to have met the other caller's row, not merely followed it");
    }

    /// <summary>
    /// Stores one bucket in the middle of the window, so the fill answers one range and records another.
    /// </summary>
    /// <param name="venue">The private venue id for the test.</param>
    /// <param name="now">The clock the seeding fill runs at.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// <c>BarGapDetector.FindMissing</c> breaks its runs at every stored bucket, so a single bucket at index
    /// 10 turns a 0..20 window into <c>[0,10)</c> and <c>[11,20)</c>. The venue then answers the first with
    /// bars and the second with nothing — a fill that both writes bars and records coverage, in one
    /// transaction, in that order. A window with a hole in it is what an earlier partial fill leaves behind,
    /// so this is a state the cache reaches on its own rather than one only a test can build.
    /// </remarks>
    private async Task SeedTheBucketThatSplitsTheWindowAsync(string venue, DateTimeOffset now)
    {
        await using TopstepXDbContext seed = _fixture.CreateContext();

        BarCacheService cache =
            ConcurrencyHarness.Cache(seed, venue, ConcurrencyHarness.Bars(10, 11), now);

        await cache.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(10, 11),
            CancellationToken.None);
    }

    /// <summary>The single coverage row the venue holds, failing the test if there is not exactly one.</summary>
    /// <param name="venue">The private venue id for the test.</param>
    /// <returns>The row.</returns>
    private async Task<BarCoverageRecord> TheOnlyCoverageRowAsync(string venue)
    {
        await using TopstepXDbContext reader = _fixture.CreateContext();

        List<BarCoverageRecord> rows = await reader.BarCoverage
            .AsNoTracking()
            .Where(c => c.Venue == venue
                && c.Instrument == ConcurrencyHarness.Symbol
                && c.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .ToListAsync();

        rows.Should().ContainSingle(
            "the composite key makes a second row for one range unrepresentable, and both callers recorded "
            + "the same range -- two rows would mean the key had been sidestepped, none would mean a write "
            + "was lost");

        return rows[0];
    }

    /// <summary>
    /// Asserts the fill that lost the race still landed the bars it answered with.
    /// </summary>
    /// <param name="venue">The private venue id for the test.</param>
    /// <returns>The task.</returns>
    /// <remarks>
    /// The bars and the ledger commit together, so a fix that let the ledger write succeed by abandoning the
    /// transaction around it would satisfy every assertion above and quietly drop ten bars. Eleven: the ten
    /// the fill answered with, plus the one the seed stored.
    /// </remarks>
    private async Task AssertTheFillStillDidItsWorkAsync(string venue)
    {
        await using TopstepXDbContext reader = _fixture.CreateContext();

        List<DateTimeOffset> stored = await reader.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == ConcurrencyHarness.Symbol
                && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .Select(b => b.BucketStart)
            .ToListAsync();

        stored.Should().BeEquivalentTo(
            Enumerable.Range(0, 11).Select(ConcurrencyHarness.Bucket),
            "the ledger write shares its transaction with the bar write, so a caller whose coverage row "
            + "survived while its bars did not has been told the range is answered when it is not");
    }
}
