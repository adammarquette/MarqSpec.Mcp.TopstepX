using System.Data;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A projection pass reads the bars and then the stored values. These pin what happens when another
/// transaction commits <b>between</b> those two reads (gh#73).
/// </summary>
/// <remarks>
/// <para>
/// <b>This tier, and only this tier.</b> The unit suite's in-memory provider has no transactions and no
/// isolation levels at all, so the defect is not merely hard to express there — it is unrepresentable. A test
/// asserting it against a fake would be green on the day the fix was reverted.
/// </para>
/// <para>
/// <b>Why it is a defect and not merely a race.</b> The reconcile added in gh#42 removes every value the pass
/// is configured to produce and did not. A pass that read the bars before a concurrent fill committed, and the
/// values after, sees values it cannot account for — and deletes them. What is lost is a number the bars fully
/// justify, and it is lost as an <i>absence</i>, which every consumer of this server correctly reads as
/// <i>cannot measure</i>.
/// </para>
/// <para>
/// Each test owns a private venue id, so the series are disjoint inside the shared container. The rebuild verb
/// walks every series it finds, which is why the catalogue is identical everywhere — see
/// <see cref="ConcurrencyHarness.Catalog"/>.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class IndicatorReconcileConcurrencyTests(SchemaFixture fixture)
{
    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    // ── The reported defect ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConcurrentFill_CannotReconcileAwayValuesTheBarsJustify()
    {
        // THE regression. Two fills of the same series over DISJOINT ranges, interleaved exactly as gh#73
        // describes: B reads the bars without seeing A's, A commits, B reads the values and does see A's.
        //
        // Disjoint ranges matter. Neither pass has any business writing over the other's rows, so the only
        // thing in the code that can remove them is the reconcile.
        string venue = ConcurrencyHarness.Venue();

        await using TopstepXDbContext aStore = _fixture.CreateContext();
        List<DateTimeOffset> committedByA = [];

        async Task FillTheFirstHalfAndCommit()
        {
            BarCacheService a = ConcurrencyHarness.Cache(aStore, venue, ConcurrencyHarness.Bars(0, 20), Now);
            await a.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, 20),
                CancellationToken.None);

            // Read back through A's own context, after A committed and before B's pass continues. This is
            // what B must not be able to remove, and taking it here rather than hard-coding a bucket list
            // keeps the assertion honest about which buckets the warm-up actually reached.
            committedByA.AddRange(await ConcurrencyHarness.BucketsWithValuesAsync(aStore, venue));
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.Before(
            "FROM \"IndicatorValues\"", venue, FillTheFirstHalfAndCommit);

        await using TopstepXDbContext bStore = _fixture.CreateContext(straddle);
        BarCacheService b = ConcurrencyHarness.Cache(bStore, venue, ConcurrencyHarness.Bars(20, 40), Now);

        await b.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(20, 40),
            CancellationToken.None);

        straddle.Fired.Should().BeTrue(
            "the interleaving is the test -- if the other fill never ran between the two reads, this passed "
            + "by not exercising anything");
        committedByA.Should().NotBeEmpty("the first fill has to have written values for there to be a loss");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        IReadOnlyList<DateTimeOffset> surviving =
            await ConcurrencyHarness.BucketsWithValuesAsync(reader, venue);

        surviving.Should().Contain(
            committedByA,
            "the concurrent fill's bars are still in the store, so every value it wrote is still justified");
    }

    [Fact]
    public async Task ARebuild_CannotReconcileAwayValuesAConcurrentFillJustCommitted()
    {
        // The same straddle, through the other call site. `rebuild-indicators` is the command an operator
        // runs precisely when they are trying to REPAIR the store, and it reconciles every series in it --
        // the same defect with a wider blast radius.
        string venue = ConcurrencyHarness.Venue();

        await using (TopstepXDbContext seed = _fixture.CreateContext())
        {
            BarCacheService baseline =
                ConcurrencyHarness.Cache(seed, venue, ConcurrencyHarness.Bars(0, 20), Now);
            await baseline.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, 20),
                CancellationToken.None);
        }

        await using TopstepXDbContext fillStore = _fixture.CreateContext();
        List<DateTimeOffset> committedByTheFill = [];

        async Task FillTheSecondHalfAndCommit()
        {
            BarCacheService fill =
                ConcurrencyHarness.Cache(fillStore, venue, ConcurrencyHarness.Bars(20, 40), Now);
            await fill.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(20, 40),
                CancellationToken.None);

            IReadOnlyList<DateTimeOffset> all =
                await ConcurrencyHarness.BucketsWithValuesAsync(fillStore, venue);
            committedByTheFill.AddRange(all.Where(b => b >= ConcurrencyHarness.Bucket(20)));
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.Before(
            "FROM \"IndicatorValues\"", venue, FillTheSecondHalfAndCommit);

        await using TopstepXDbContext rebuildStore = _fixture.CreateContext(straddle);

        IndicatorRebuilder rebuilder = new(
            rebuildStore,
            ConcurrencyHarness.Projector(rebuildStore),
            ConcurrencyHarness.Registry(),
            new FakeTimeProvider(Now),
            NullLogger<IndicatorRebuilder>.Instance);

        await rebuilder.RebuildAsync(ConcurrencyHarness.Symbol, CancellationToken.None);

        straddle.Fired.Should().BeTrue("the fill has to land between the rebuild's two reads");
        committedByTheFill.Should().NotBeEmpty("the fill has to have written values for there to be a loss");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        IReadOnlyList<DateTimeOffset> surviving =
            await ConcurrencyHarness.BucketsWithValuesAsync(reader, venue);

        surviving.Should().Contain(
            committedByTheFill,
            "a rebuild replays what the bars say; it must not delete values whose bars it simply did not read");
    }

    // ── The property nothing else enforces ───────────────────────────────────────────────────────────

    [Fact]
    public async Task APassThatDidNotReadTheWholeSeries_RefusesRatherThanDeletingOutsideWhatItRead()
    {
        // The reconcile is UNSCOPED BY BUCKET RANGE. That is sound only because every pass reads the whole
        // series -- true at both call sites today and enforced by nothing until now. A pass reading less than
        // the series holds is exactly the shape a future `ProjectAsync(range)` would have, and this pins that
        // it fails loudly instead of deleting every value outside the range it read.
        //
        // READ COMMITTED here is deliberate, and is how the narrowing is produced without inventing a range
        // parameter that does not exist: a second connection commits a bar between the two reads, so the pass
        // demonstrably read less than the store holds.
        string venue = ConcurrencyHarness.Venue();
        await SeedSeriesWithValuesThenOrphanTheTailAsync(venue);

        InterleavingInterceptor extraBar = InterleavingInterceptor.After(
            "FROM \"Bars\"", venue, () => CommitOneMoreBarAsync(venue));

        await using TopstepXDbContext store = _fixture.CreateContext(extraBar);
        await using IDbContextTransaction transaction = await store.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);

        Func<Task> project = () => ConcurrencyHarness.Projector(store).ProjectAsync(
            venue,
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            Now,
            CancellationToken.None);

        (await project.Should().ThrowAsync<InvalidOperationException>(
            "a pass that read part of a series must not reconcile over all of it"))
            .WithMessage("*whole series*");

        extraBar.Fired.Should().BeTrue("the narrowing is what is under test");
        await transaction.RollbackAsync();

        await using TopstepXDbContext reader = _fixture.CreateContext();
        IReadOnlyList<DateTimeOffset> surviving =
            await ConcurrencyHarness.BucketsWithValuesAsync(reader, venue);

        surviving.Should().Contain(
            ConcurrencyHarness.Bucket(35),
            "refusing means nothing was deleted, not that the deletion happened and then complained");
    }

    [Fact]
    public async Task APassOverOneSnapshot_StillReconcilesTheValuesItsBarsNoLongerJustify()
    {
        // The control for the test above, and the reason the guard is not simply "never delete". Identical
        // setup, identical interleaving -- only the isolation level differs. One snapshot means the pass DID
        // read the whole series as of that snapshot, so the orphaned tail is removed exactly as ADR-0011
        // requires. Without this, "refuses to reconcile" would be indistinguishable from "no longer
        // reconciles".
        string venue = ConcurrencyHarness.Venue();
        await SeedSeriesWithValuesThenOrphanTheTailAsync(venue);

        InterleavingInterceptor extraBar = InterleavingInterceptor.After(
            "FROM \"Bars\"", venue, () => CommitOneMoreBarAsync(venue));

        await using TopstepXDbContext store = _fixture.CreateContext(extraBar);
        await using IDbContextTransaction transaction = await store.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, CancellationToken.None);

        int changed = await ConcurrencyHarness.Projector(store).ProjectAsync(
            venue,
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            Now,
            CancellationToken.None);

        await store.SaveChangesAsync();
        await transaction.CommitAsync();

        extraBar.Fired.Should().BeTrue();
        changed.Should().BeGreaterThan(0, "the orphaned tail has to be removed");

        await using TopstepXDbContext reader = _fixture.CreateContext();
        IReadOnlyList<DateTimeOffset> surviving =
            await ConcurrencyHarness.BucketsWithValuesAsync(reader, venue);

        surviving.Should().NotContain(
            ConcurrencyHarness.Bucket(35),
            "no bar stands behind that bucket any more, so no value may either");
        surviving.Should().Contain(
            ConcurrencyHarness.Bucket(20),
            "the buckets whose bars remain keep their values");
    }

    // ── Setup ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills forty buckets, then deletes the last ten bars, leaving values a pass is obliged to remove.
    /// </summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <remarks>
    /// There has to be something to delete, or the reconcile never reaches the point where its scope matters.
    /// Deleting bars rather than restating them is the cheapest way to make a stored value unjustified — and
    /// it is a real state, since nothing links <c>IndicatorValues</c> to <c>Bars</c> by a foreign key.
    /// </remarks>
    private async Task SeedSeriesWithValuesThenOrphanTheTailAsync(string venue)
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

    /// <summary>Commits one further bar from another connection, so the reading pass has seen less.</summary>
    /// <param name="venue">The private venue id for this test.</param>
    private async Task CommitOneMoreBarAsync(string venue)
    {
        await using TopstepXDbContext other = _fixture.CreateContext();

        other.Bars.Add(new BarRecord
        {
            Venue = venue,
            Instrument = ConcurrencyHarness.Symbol,
            ResolutionMinutes = ConcurrencyHarness.ResolutionMinutes,
            BucketStart = ConcurrencyHarness.Bucket(45),
            Open = 5_100m,
            High = 5_101.25m,
            Low = 5_099.25m,
            Close = 5_100.5m,
            Volume = 1_234,
            ContractId = ConcurrencyHarness.ContractId,
            RecordedAt = Now,
        });

        await other.SaveChangesAsync();
    }
}
