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
/// it collided on are ones the other fill committed — the store is not damaged, only the answer is lost.
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
    /// <summary>How far a restated bar moves, so which fill wrote a row is visible in the row.</summary>
    private static readonly decimal Restatement = 7.5m;

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

        // AFTER, and matched on a column only the overlap read selects. The ordering is the whole test: the
        // overlap read is this transaction's FIRST statement, so it is where the snapshot is taken, and the
        // other fill has to commit after that and before the write. Firing on the read that opens
        // `GetBarsAsync` instead would let the other fill commit before this one has a snapshot at all, and
        // the overlap would simply be visible.
        //
        // `"Volume"` is the discriminator because the read that opens `GetBarsAsync` projects to `BucketStart`
        // alone and the coverage read is a different table: the first SELECT naming a bar's volume is the
        // overlap read.
        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"Volume\"", venue, FillTheOverlappingRangeAndCommit);

        await using TopstepXDbContext store = _fixture.CreateContext(straddle);
        BarCacheService fill = ConcurrencyHarness.Cache(store, venue, Restated(10, 30), Now);

        Func<Task> read = () => fill.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(10, 30),
            CancellationToken.None);

        await read.Should().NotThrowAsync(
            "an overlapping re-fetch updates the buckets it already holds (R-1.6), and a concurrent fill is "
            + "an overlapping re-fetch -- the caller asked an ordinary question and the rows it collided on "
            + "are in the store");

        straddle.Fired.Should().BeTrue(
            "the interleaving is the test -- if the other fill never ran between the snapshot and the write, "
            + "this passed by not exercising anything");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        List<BarRecord> stored = await reader.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == ConcurrencyHarness.Symbol
                && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .OrderBy(b => b.BucketStart)
            .ToListAsync();

        stored.Select(b => b.BucketStart).Should().BeEquivalentTo(
            Enumerable.Range(0, 30).Select(ConcurrencyHarness.Bucket),
            "the union of the two ranges is present, each bucket once -- neither fill's rows were lost");

        for (int index = 10; index < 20; index++)
        {
            BarRecord row = stored.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index));
            row.Close.Should().Be(
                Restated(index, index + 1)[0].Close,
                "bucket {0} was written by both fills, and the losing insert must have UPDATED the row to "
                + "the later fill's values rather than being silently discarded -- a swallowed insert is "
                + "worse than the duplicate key it replaces, because it is quiet",
                index);
        }

        for (int index = 0; index < 10; index++)
        {
            BarRecord row = stored.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index));
            row.Close.Should().Be(
                ConcurrencyHarness.Bars(index, index + 1)[0].Close,
                "bucket {0} is outside the later fill's range, so the earlier fill's values stand",
                index);
        }
    }

    /// <summary>
    /// The harness's bars at restated prices, so which fill wrote a row can be read off the row.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The bars.</returns>
    /// <remarks>
    /// A revision is what the venue actually does to a recent bar, so the second fill differing from the first
    /// over the overlap is the ordinary case rather than a contrivance — and without it "the later fill's
    /// values stand" and "the earlier fill's values stand" are the same assertion.
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
