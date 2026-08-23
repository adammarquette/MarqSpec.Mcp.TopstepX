using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// `R-1.6` says an overlapping re-fetch <b>updates</b> the buckets it already holds. These pin that it does so
/// when the overlap is another fill running at the same time, not only when it is the same fill running twice
/// (gh#103).
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier, and only this tier.</b> The claim is about what one snapshot can see of another
/// transaction's committed rows, and the unit suite's in-memory provider has neither snapshots nor
/// transactions — a test of it there would be green on the day the fix was reverted.
/// </para>
/// <para>
/// <b>Why it is a defect and not merely a race.</b> A losing insert does not lose a row: it aborts the whole
/// transaction, so the bars, the coverage ledger and the projection over the same series all roll back, and
/// the caller of <c>get_bars</c> is handed a database error for asking a perfectly ordinary question. The rows
/// it collided on are ones the other fill committed — so the store is fine, and only the answer is lost.
/// </para>
/// <para>
/// Each test owns a private venue id, so the series are disjoint inside the shared container — see
/// <see cref="ConcurrencyHarness"/>.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class BarUpsertConcurrencyTests(SchemaFixture fixture)
{
    /// <summary>How far a restated bar moves, so which fill wrote a row can be read off the row.</summary>
    private const decimal Restatement = 7.5m;

    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    [Fact]
    public async Task TwoFillsOfOverlappingRanges_BothLand_AndTheLaterOnesValuesStand()
    {
        // OVERLAPPING ranges -- 0..20 and 10..30 -- which is what separates this from the disjoint-range
        // collisions in SerializationFailureTests. Ten buckets are written by both fills, and the one that
        // reads its overlap from a snapshot taken before the other committed finds them absent when they are
        // not.
        string venue = ConcurrencyHarness.Venue();

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task FillTheOverlappingRangeAndCommit()
        {
            BarCacheService other =
                ConcurrencyHarness.Cache(otherStore, venue, ConcurrencyHarness.Bars(0, 20), Now);
            await other.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, 20),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = Straddle(venue, FillTheOverlappingRangeAndCommit);
        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService fill = ConcurrencyHarness.Cache(store, venue, Restated(10, 30), Now, log);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(10, 30),
            CancellationToken.None);

        await read.Should().NotThrowAsync(
            "an overlapping re-fetch updates the buckets it already holds (R-1.6), and a concurrent fill is "
            + "an overlapping re-fetch -- the caller asked an ordinary question, and the rows it collided on "
            + "are in the store");

        AssertTheFillsActuallyCollided(straddle, log);

        IReadOnlyList<BarRecord> stored = await StoredSeriesAsync(venue);

        stored.Select(b => b.BucketStart).Should().BeEquivalentTo(
            Enumerable.Range(0, 30).Select(ConcurrencyHarness.Bucket),
            "the union of the two ranges is present, each bucket exactly once -- neither fill's rows were "
            + "lost, and the composite key makes a duplicate unrepresentable");

        for (int index = 10; index < 20; index++)
        {
            stored.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index)).Close.Should().Be(
                Restated(index, index + 1)[0].Close,
                "bucket {0} was written by both fills, so the losing insert must have UPDATED the row to the "
                + "later fill's values rather than being discarded -- a swallowed insert is worse than the "
                + "duplicate key it replaces, because it is quiet",
                index);
        }

        for (int index = 0; index < 10; index++)
        {
            stored.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index)).Close.Should().Be(
                ConcurrencyHarness.Bars(index, index + 1)[0].Close,
                "bucket {0} is outside the later fill's range, so the earlier fill's values stand",
                index);
        }
    }

    [Fact]
    public async Task AnOverlapTheTwoFillsAgreeOn_IsNotRewritten()
    {
        // The revision semantics, under the same collision. An unchanged bucket is SKIPPED rather than
        // rewritten, and that skip is load-bearing: a bucket rewritten with the numbers it already held still
        // moves RecordedAt, which is the store's answer to "when was this last revised" -- and it still
        // counts as a write, which sends the whole series back through the projection for nothing.
        //
        // The two fills run at clocks one minute apart, because RecordedAt is the only thing that
        // distinguishes a row left alone from a row written again with identical values.
        string venue = ConcurrencyHarness.Venue();
        DateTimeOffset later = Now.AddMinutes(1);

        await using TopstepXDbContext otherStore = _fixture.CreateContext();

        async Task FillTheOverlappingRangeAndCommit()
        {
            BarCacheService other =
                ConcurrencyHarness.Cache(otherStore, venue, ConcurrencyHarness.Bars(0, 20), Now);
            await other.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, 20),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = Straddle(venue, FillTheOverlappingRangeAndCommit);
        CapturingLogger<BarCacheService> log = new();
        await using TopstepXDbContext store = _fixture.CreateContext(straddle);

        // The SAME bars over the overlap, which is what makes this the unchanged case rather than the revision
        // case: buckets 10..19 are byte-for-byte what the other fill committed, and 20..29 are genuinely new.
        BarCacheService fill =
            ConcurrencyHarness.Cache(store, venue, ConcurrencyHarness.Bars(10, 30), later, log);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(10, 30),
            CancellationToken.None);

        await read.Should().NotThrowAsync();
        AssertTheFillsActuallyCollided(straddle, log);

        IReadOnlyList<BarRecord> stored = await StoredSeriesAsync(venue);

        for (int index = 0; index < 20; index++)
        {
            stored.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index)).RecordedAt.Should().Be(
                Now,
                "bucket {0} already held exactly what the second fill answered with, so it was not written "
                + "again -- an unchanged bar is skipped, not rewritten",
                index);
        }

        for (int index = 20; index < 30; index++)
        {
            stored.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index)).RecordedAt.Should().Be(
                later,
                "bucket {0} was new to the store, so the second fill wrote it -- the skip must not have "
                + "swallowed the buckets that genuinely changed",
                index);
        }
    }

    /// <summary>
    /// Places another fill's whole transaction between this one's snapshot and its write.
    /// </summary>
    /// <param name="venue">The private venue id, which is how the right series is recognised.</param>
    /// <param name="otherFill">The other fill, run to completion and committed.</param>
    /// <returns>The interceptor. Hand it to the context, and assert it fired.</returns>
    /// <remarks>
    /// <b>AFTER, and matched on a column only the overlap read selects.</b> The ordering is the whole test:
    /// the overlap read is the fill transaction's <i>first</i> statement, so it is where the snapshot is
    /// taken, and the other fill has to commit after that and before the write. Firing on the read that opens
    /// <c>GetBarsAsync</c> instead would let the other fill commit before this one has a snapshot at all, and
    /// the overlap would simply be visible — the interleaving would run and prove nothing.
    /// <para>
    /// <c>"Volume"</c> is the discriminator because the read that opens <c>GetBarsAsync</c> projects to the
    /// bucket alone and the coverage read is a different table: the first <c>SELECT</c> naming a bar's volume
    /// is the overlap read.
    /// </para>
    /// </remarks>
    private static InterleavingInterceptor Straddle(string venue, Func<Task> otherFill) =>
        InterleavingInterceptor.After("\"Volume\"", venue, otherFill);

    /// <summary>
    /// Asserts the two fills met, rather than the test having quietly run them one after the other.
    /// </summary>
    /// <param name="straddle">The interceptor that placed the other fill.</param>
    /// <param name="log">What the fill said it did.</param>
    /// <remarks>
    /// Two separate claims, and both are needed. The interceptor firing says the other transaction ran at the
    /// chosen point; the retry says this one's write actually <b>collided</b> with it. Postgres refuses a
    /// conflicting <c>ON CONFLICT DO UPDATE</c> against a row committed after the snapshot with a
    /// <c>40001</c>, which `R-2.10` retries once — and a retry leaves no other trace, so asserting on the
    /// outcome alone would pass just as well against a run where nothing collided.
    /// </remarks>
    private static void AssertTheFillsActuallyCollided(
        InterleavingInterceptor straddle,
        CapturingLogger<BarCacheService> log)
    {
        straddle.Fired.Should().BeTrue(
            "the interleaving is the test -- if the other fill never ran between the snapshot and the write, "
            + "this passed by not exercising anything");

        log.Messages.Should().ContainMatch(
            "*serialization failure*",
            "the write has to have met the other fill's rows, not merely followed them");
    }

    /// <summary>The whole stored series, as the store holds it after both fills.</summary>
    /// <param name="venue">The private venue id for the test.</param>
    /// <returns>The rows, ascending.</returns>
    private async Task<IReadOnlyList<BarRecord>> StoredSeriesAsync(string venue)
    {
        await using TopstepXDbContext reader = _fixture.CreateContext();

        return await reader.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == ConcurrencyHarness.Symbol
                && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .OrderBy(b => b.BucketStart)
            .ToListAsync();
    }

    /// <summary>
    /// The harness's bars at restated prices, so which fill wrote a row can be read off the row.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// A restatement is what the venue actually does to a recent bar, so the second fill differing from the
    /// first over the overlap is the ordinary case rather than a contrivance — and without it, "the later
    /// fill's values stand" and "the earlier fill's values stand" are the same assertion.
    /// </remarks>
    private static IReadOnlyList<Bar> Restated(int fromIndex, int toIndexExclusive) =>
    [
        .. ConcurrencyHarness.Bars(fromIndex, toIndexExclusive).Select(b => b with
        {
            Open = b.Open + Restatement,
            High = b.High + Restatement,
            Low = b.Low + Restatement,
            Close = b.Close + Restatement,
            Volume = b.Volume + 1,
        }),
    ];
}
