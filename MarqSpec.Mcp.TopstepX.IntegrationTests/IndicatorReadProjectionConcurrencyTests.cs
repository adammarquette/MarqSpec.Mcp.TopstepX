using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Two simultaneous cold reads of one series produce <b>one</b> set of writes, and the trigger that made a
/// read able to write at all is gh#246's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing serialises this, and that is the claim worth stating precisely.</b> The issue asks whether "the
/// advisory-lock machinery" covers a read-initiated write. There is no such machinery:
/// <see href="../documentation/adr/0012-fills-are-not-serialised.md">ADR-0012</see> measured both lock shapes
/// and rejected both — a session lock outlives the request that took it, and a transaction-scoped one is
/// granted over a snapshot taken before it was granted. What makes a pair of cold reads safe is the store:
/// both write with one <c>ON CONFLICT … DO UPDATE</c> under <see cref="SeriesUnitOfWork.Isolation"/>, so the
/// loser meets a <c>40001</c>, and the single retry re-derives against a store that now holds the winner's
/// values, recomputes them to the same numbers, and writes nothing.
/// </para>
/// <para>
/// <b>This tier, and only this tier.</b> The in-memory provider has no transactions and no isolation levels,
/// so the interleaving is not merely hard to hit there — it is unrepresentable, and a test of it would be
/// green on the day the retry was deleted.
/// </para>
/// <para>
/// <b>The two passes run on different clocks, and that is the assertion.</b> <c>RecordedAt</c> moves only when
/// a value actually changes (`R-2.8`, ADR-0006), so if the loser's retry wrote anything the rows would carry
/// <i>its</i> instant. Every row carrying the winner's instant is the direct statement that exactly one
/// projection landed.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class IndicatorReadProjectionConcurrencyTests(SchemaFixture fixture)
{
    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    /// <summary>The instant the losing pass would stamp, if it wrote anything. It must not.</summary>
    private static DateTimeOffset Later => Now.AddMinutes(7);

    [Fact]
    public async Task TwoConcurrentColdReadsOfOneSeries_ProduceOneProjection()
    {
        // Both readers find a series whose bars are stored and whose values are not -- the state an added
        // indicator, or a moved period, leaves behind, because (Indicator, Period) IS the storage key.
        //
        // The interleaving is placed AFTER the projector's bar read, which is the first statement inside the
        // read's transaction and therefore where its snapshot is fixed. `"Volume"` is the discriminator: the
        // probe counts bars and never names a column, and the values read is against IndicatorValues.
        string venue = ConcurrencyHarness.Venue();
        await SeedBarsWithNoValuesAsync(venue);

        await using TopstepXDbContext winnerStore = _fixture.CreateContext();

        async Task TheOtherReadProjectsTheSameSeries()
        {
            bool projected = await ConcurrencyHarness.Indicators(winnerStore, Now)
                .EnsureProjectedAsync(
                    venue,
                    ConcurrencyHarness.Instrument,
                    ConcurrencyHarness.ResolutionMinutes,
                    CancellationToken.None);

            projected.Should().BeTrue("the winner is the pass that finds the series cold and replays it");
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"Volume\"", venue, TheOtherReadProjectsTheSameSeries);

        CapturingLogger<IndicatorCacheService> log = new();
        await using TopstepXDbContext loserStore = _fixture.CreateContext(straddle);

        Func<Task> read = () => ConcurrencyHarness.Indicators(loserStore, Later, log)
            .EnsureProjectedAsync(
                venue,
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                CancellationToken.None);

        await read.Should().NotThrowAsync(
            "an ordinary get_indicators call must not hand its caller a database error because another "
            + "caller asked the same question at the same moment");

        straddle.Fired.Should().BeTrue(
            "the collision IS the test -- if the other read never ran inside this one's transaction, this "
            + "passed by exercising nothing");

        log.Messages.Should().ContainMatch(
            "*serialization failure*",
            "the losing pass has to have MET the winner's rows. A retry leaves no other trace, and asserting "
            + "only that the call succeeded would pass just as well on a run where nothing collided");

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), DateTimeOffset> stamps =
            await RecordedAtAsync(venue);

        stamps.Should().NotBeEmpty("a race that wrote nothing at all would satisfy everything below vacuously");
        stamps.Values.Should().AllBeEquivalentTo(
            Now,
            "one projection landed and the other found its work already done. A row carrying the loser's "
            + "clock would mean both passes wrote -- which is the duplicated work this criterion forbids, and "
            + "would also report the series as revised at a moment no number changed");
    }

    [Fact]
    public async Task AColdRead_ProjectsInsideATransaction_AndTheProjectorsGuardAcceptsIt()
    {
        // The projector REFUSES to run outside a transaction: its values are written by a statement the store
        // runs as it is sent while its removals wait for SaveChanges, so outside one the halves cannot commit
        // together. That guard is relational-only, so the unit tier cannot reach it -- a read-time trigger
        // that forgot SeriesUnitOfWork would be green there and would throw on the first real call.
        string venue = ConcurrencyHarness.Venue();
        await SeedBarsWithNoValuesAsync(venue);

        await using TopstepXDbContext store = _fixture.CreateContext();

        bool projected = await ConcurrencyHarness.Indicators(store, Now).EnsureProjectedAsync(
            venue,
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            CancellationToken.None);

        projected.Should().BeTrue();

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> fromRead =
            await ValuesAsync(venue);
        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal> uncontended =
            await UncontendedFillOfAsync(ConcurrencyHarness.Bars(0, 20));

        fromRead.Should().BeEquivalentTo(
            uncontended,
            "a read-triggered projection is the same replay the fill path runs, so it must land on exactly "
            + "what an ordinary fill of the same bars produces -- rebuild is still replay (ADR-0006)");
    }

    [Fact]
    public async Task AWarmSeries_IsNotReprojectedAndItsTimestampsDoNotMove()
    {
        // The awkward correct input: the case every read after the first one meets. A trigger keyed on
        // anything looser than "the catalogue computes a pair the store does not hold" would replay here, and
        // the only visible symptom would be RecordedAt drifting on a series nothing revised.
        string venue = ConcurrencyHarness.Venue();
        await SeedAsync(venue, ConcurrencyHarness.Bars(0, 20));

        IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), DateTimeOffset> before =
            await RecordedAtAsync(venue);

        await using TopstepXDbContext store = _fixture.CreateContext();
        IndicatorCacheService indicators = ConcurrencyHarness.Indicators(store, Later);

        bool projected = await indicators.EnsureProjectedAsync(
            venue,
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            CancellationToken.None);

        projected.Should().BeFalse("the fill already projected every pair the catalogue computes");
        indicators.Projections.Should().Be(0, "a warm read must open no transaction at all");

        (await RecordedAtAsync(venue)).Should().BeEquivalentTo(
            before,
            "nothing changed, so nothing may be restamped");
    }

    // ── Setup and oracles ────────────────────────────────────────────────────────────────────────────

    /// <summary>Fills a series through the ordinary cache-aside bar read.</summary>
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
    /// Bars with no values under a key the catalogue computes is exactly what gh#246 is about, and it is an
    /// ordinary state rather than a contrivance: moving <c>IndicatorOptions.AtrPeriod</c>, or adding an
    /// indicator, leaves every stored bar without a row under the new key.
    /// </remarks>
    private async Task SeedBarsWithNoValuesAsync(string venue)
    {
        await SeedAsync(venue, ConcurrencyHarness.Bars(0, 20));

        await using TopstepXDbContext store = _fixture.CreateContext();

        List<IndicatorValueRecord> values = await store.IndicatorValues
            .Where(v => v.Venue == venue)
            .ToListAsync();

        store.IndicatorValues.RemoveRange(values);
        await store.SaveChangesAsync();
    }

    /// <summary>
    /// What one uncontended fill of a bar set produces, as the oracle for what a read-triggered one must equal.
    /// </summary>
    /// <param name="bars">The bars the venue serves.</param>
    /// <returns>The stored values, keyed.</returns>
    /// <remarks>
    /// Derived by running the fill over its own private venue rather than by restating the arithmetic here.
    /// The claim under test is not what an RSI is — that is pinned by hand-checked fixtures in the unit tier —
    /// it is that the <i>trigger</i> does not change the answer, and the only honest statement of that is
    /// "the same as the trigger it is joining".
    /// </remarks>
    private async Task<IReadOnlyDictionary<(string Indicator, int Period, DateTimeOffset Bucket), decimal>>
        UncontendedFillOfAsync(IReadOnlyList<Bar> bars)
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
