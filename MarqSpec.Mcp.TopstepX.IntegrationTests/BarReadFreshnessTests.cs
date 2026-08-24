using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// What a caller is handed has to be what the store holds, across two calls sharing one scope (gh#103).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this became reachable.</b> The bar write is now raw SQL, so it does not pass through the change
/// tracker. Under the in-memory merge it did — <c>row.Open = bar.Open</c> mutated the very instance in the
/// identity map — so the map and the store could not disagree. With the write moved to <c>ON CONFLICT</c>
/// that coupling is gone, and any <b>tracking</b> read of <c>Bars</c> can hand back a pre-revision copy that
/// EF resolves from the identity map instead of the row it just read.
/// </para>
/// <para>
/// <b>Why one call is not enough to see it.</b> Within a single <c>GetBarsAsync</c> the raw write precedes
/// the final read and nothing tracked those bars earlier in the call. The exposure is <b>across calls in one
/// scope</b>, and that is the ordinary arrangement rather than an exotic one: <c>TopstepXDbContext</c> and
/// <c>BarCacheService</c> are both scoped, and <c>SnapshotTools</c> makes two deliberately overlapping bar
/// reads per resolution inside one scope.
/// </para>
/// <para>
/// <b>This tier, and only this tier.</b> The claim is about what a relational provider's identity map does
/// with a row written by SQL it never saw. The unit suite's in-memory provider takes
/// <c>UpsertInMemoryAsync</c>, whose write goes <i>through</i> the tracker — the divergence this pins is not
/// merely hard to reproduce there, it is unrepresentable.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class BarReadFreshnessTests(SchemaFixture fixture)
{
    /// <summary>Far enough that a stale copy is unmistakable rather than a rounding argument.</summary>
    private const decimal Restatement = 500m;

    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    [Fact]
    public async Task ARestatementOfBucketsAnEarlierCallRead_ReachesTheNextCallInTheSameScope()
    {
        // ONE context across both calls, which is what production runs: both the context and the service are
        // scoped, so two tool calls in one request share this arrangement.
        //
        // The route is entirely ordinary. The second call asks for a WIDER window, so buckets 0..9 are
        // missing and a fetch happens for them; the venue answers that fetch with bars beyond the slice it
        // was asked for, including restated 10..19. FetchAsync drops still-forming bars and nothing else --
        // it does not clip to the slice -- so the restatement reaches the store. The first call has already
        // tracked 10..19, and a tracking read resolves those keys from the identity map rather than from the
        // rows it just read.
        //
        // BarGapDetector.FindMissing breaks its runs at every stored bucket, which is why the venue has to
        // overreach for this to be reachable at all: a fetch is only ever requested for buckets the store
        // does not hold, so a restatement of buckets it DOES hold can only arrive as a bonus on someone
        // else's slice.
        string venue = ConcurrencyHarness.Venue();
        DateTimeOffset later = Now.AddMinutes(1);

        await using TopstepXDbContext store = _fixture.CreateContext();

        BarCacheService first = ConcurrencyHarness.Cache(store, venue, ConcurrencyHarness.Bars(10, 20), Now);

        BarReadResult firstRead = await first.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(10, 20),
            CancellationToken.None);

        firstRead.Bars.Should().HaveCount(
            10,
            "the first call is what puts buckets 10..19 in this context's identity map -- if it read nothing, "
            + "there is no stale copy for the second call to be handed and the test proves nothing");

        IReadOnlyList<Bar> answer = [.. ConcurrencyHarness.Bars(0, 10), .. Restated(10, 20)];

        BarCacheService second = ConcurrencyHarness.Cache(
            store, venue, answer, later, answersBeyondTheSlice: true);

        BarReadResult secondRead = await second.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(0, 20),
            CancellationToken.None);

        // Read first, so a failure below is unambiguous. If the store holds the restatement and the caller
        // does not, the write is fine and the READ is what is wrong -- which is the whole finding, and the
        // opposite diagnosis would send the next agent to rewrite the upsert.
        await using TopstepXDbContext fresh = _fixture.CreateContext();

        List<BarRecord> committed = await fresh.Bars
            .AsNoTracking()
            .Where(b => b.Venue == venue
                && b.Instrument == ConcurrencyHarness.Symbol
                && b.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .OrderBy(b => b.BucketStart)
            .ToListAsync();

        for (int index = 10; index < 20; index++)
        {
            committed.Single(b => b.BucketStart == ConcurrencyHarness.Bucket(index)).Close.Should().Be(
                Restated(index, index + 1)[0].Close,
                "bucket {0} was restated by the venue and the write is not in question -- if this fails the "
                + "defect is in the upsert, not in the read the next assertion is about",
                index);
        }

        for (int index = 10; index < 20; index++)
        {
            secondRead.Bars.Single(b => b.OpenTime == ConcurrencyHarness.Bucket(index)).Close.Should().Be(
                Restated(index, index + 1)[0].Close,
                "bucket {0} is committed at the restated price, so the call that just wrote it must not "
                + "answer with the copy the earlier call left in the identity map -- a caller handed a "
                + "superseded price has no way to tell, and every indicator derived from it inherits it",
                index);
        }

        for (int index = 0; index < 10; index++)
        {
            secondRead.Bars.Single(b => b.OpenTime == ConcurrencyHarness.Bucket(index)).Close.Should().Be(
                ConcurrencyHarness.Bars(index, index + 1)[0].Close,
                "bucket {0} is what the second call actually asked for, and it was never tracked before -- "
                + "it must still be answered from the rows the store holds",
                index);
        }
    }

    /// <summary>
    /// The harness's bars at restated prices, far enough out that a stale copy cannot be mistaken for one.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The bars.</returns>
    private static IReadOnlyList<Bar> Restated(int fromIndex, int toIndexExclusive) =>
    [
        .. ConcurrencyHarness.Bars(fromIndex, toIndexExclusive).Select(b => b with
        {
            Open = b.Open + Restatement,
            High = b.High + Restatement,
            Low = b.Low + Restatement,
            Close = b.Close + Restatement,
        }),
    ];
}
