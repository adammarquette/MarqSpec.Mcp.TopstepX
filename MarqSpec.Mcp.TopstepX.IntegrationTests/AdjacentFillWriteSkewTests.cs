using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Two fills of one series over <b>adjacent</b> ranges, and what the later one's projection is seeded from
/// (gh#104, <see href="../documentation/adr/0012-fills-are-not-serialised.md">ADR-0012</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>These characterise a defect this repository decided to accept, which is why they exist at all.</b>
/// ADR-0012 declined to serialise fills per series. A defect nobody fixed still has to be shown to behave the
/// way the record claims it behaves, or "stale, not lost" is a hope rather than a property — and the next
/// agent reading that sentence has nothing to check it against.
/// </para>
/// <para>
/// <b>What goes wrong.</b> A fills buckets 0–19 and B fills 20–39. B's transaction fixes its snapshot before
/// A commits, so B projects over a series that <i>starts</i> at bucket 20 as far as it can tell. It is not
/// wrong about that: from inside its own snapshot, seeding at the first bar it can see is the same rule a
/// genuinely new series gets, and there is nothing in the view that distinguishes the two. The values it
/// writes are therefore smoothed from bucket 20 rather than carried forward from bucket 0, and the warm-up
/// buckets of its imagined segment get no value at all.
/// </para>
/// <para>
/// <b>Nothing tells either fill it lost.</b> Their write sets are disjoint — different bars, different
/// coverage rows, different indicator keys — so there is no <c>40001</c>, nothing to retry and no log line.
/// That is the definition of write skew, and it is why <c>SeriesUnitOfWork</c>'s retry cannot reach this: a
/// retry needs a refusal, and the store has no reason to refuse. The first test asserts the absence of that
/// retry, because it is the mechanism rather than an incidental.
/// </para>
/// <para>
/// <b>This tier, and only this tier.</b> The claim is about two transactions and one snapshot straddling the
/// other's commit. The unit suite's in-memory provider has neither transactions nor isolation levels, so the
/// defect is unrepresentable there rather than merely awkward.
/// </para>
/// <para>
/// The expected values are taken from a <b>second, unraced series</b> rather than computed by hand, and that
/// is deliberate: what is under test is not what an ATR is — the indicator suites pin that with hand-checked
/// arithmetic — but that the raced series <i>disagrees</i> with the same bars filled in one pass, and stops
/// disagreeing after another pass. Reproducibility is what makes the comparison legitimate: by ADR-0006 the
/// same bars must project to the same numbers, so any difference between the two venues is the race and
/// nothing else.
/// </para>
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class AdjacentFillWriteSkewTests(SchemaFixture fixture)
{
    /// <summary>The bucket the two ranges meet at. A fills up to it; B fills from it.</summary>
    private const int Seam = 20;

    /// <summary>One past the last bucket the two fills cover between them.</summary>
    private const int End = 40;

    private readonly SchemaFixture _fixture = fixture;

    /// <summary>Far enough past every bucket these tests use that none of them is still forming.</summary>
    private static DateTimeOffset Now => ConcurrencyHarness.Bucket(60);

    [Fact]
    public async Task TwoAdjacentFills_LeaveTheLATERHalfSeededFromItsOwnFirstBar_AndTheSeamBucketsUnmeasured()
    {
        // THE defect gh#104 is about, driven rather than described.
        //
        // The interleaving is placed AFTER the bar write's overlap read, which is the first statement inside
        // B's transaction and therefore where its snapshot is fixed. `"Volume"` is the discriminator for the
        // reason BarUpsertConcurrencyTests gives: the read that opens GetBarsAsync projects to the bucket
        // alone, so it carries no such column, and the interceptor fires only on its first match.
        //
        // Firing BEFORE that read instead would let A commit before B has a snapshot at all, and B would
        // simply see the whole series -- a green run that exercised nothing.
        string venue = ConcurrencyHarness.Venue();
        string unraced = await FilledInOnePassAsync(0, End);

        await using TopstepXDbContext aStore = _fixture.CreateContext();

        async Task TheEarlierHalfLandsWhileThisOneHoldsItsSnapshot()
        {
            BarCacheService a = ConcurrencyHarness.Cache(
                aStore, venue, ConcurrencyHarness.Bars(0, Seam), Now);
            await a.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, Seam),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"Volume\"", venue, TheEarlierHalfLandsWhileThisOneHoldsItsSnapshot);

        CapturingLogger<BarCacheService> saidWhatItDid = new();

        await using TopstepXDbContext bStore = _fixture.CreateContext(straddle);
        BarCacheService b = ConcurrencyHarness.Cache(
            bStore, venue, ConcurrencyHarness.Bars(Seam, End), Now, saidWhatItDid);

        await b.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(Seam, End),
            CancellationToken.None);

        straddle.Fired.Should().BeTrue(
            "the interleaving is the test -- if the other fill never ran inside this one's transaction, this "
            + "passed by exercising nothing");

        saidWhatItDid.Messages.Should().NotContain(
            message => message.Contains("serialization failure", StringComparison.Ordinal),
            "and the collision has to have been SILENT: the two fills share no bar, no coverage row and no "
            + "indicator key, so the store has nothing to refuse and R-2.10's retry never runs. A 40001 here "
            + "would mean this test is observing the retry rather than the write skew");

        IReadOnlyDictionary<Key, decimal> raced = await ValuesAsync(venue);
        IReadOnlyDictionary<Key, decimal> expected = await ValuesAsync(unraced);

        expected.Should().NotBeEmpty("the unraced series is the yardstick, so it has to have values");

        // THE VISIBLE SCAR, and it lands in BOTH the ways this repository cares about, on one bucket.
        //
        // A SMOOTHED indicator restarts its warm-up at the seam, so it produces nothing there. That is the
        // honest answer for a series that begins at the seam, and B has no way to know its series does not:
        // "the first bar I can see" is the same rule a genuinely new series gets, and nothing in the snapshot
        // tells the two apart. The cost is an absence, which R-2.3 makes every caller read as CANNOT MEASURE
        // over a bucket nineteen bars of history fully justify.
        Key atrAtTheSeam = new("atr", 3, ConcurrencyHarness.Bucket(Seam));

        expected.Should().ContainKey(
            atrAtTheSeam, "the seam bucket is measurable: nineteen bars stand in front of it");
        raced.Should().NotContainKey(
            atrAtTheSeam,
            "but the fill that wrote it could not see them, so it restarted the warm-up at the seam and "
            + "reported cannot-measure over a bucket that can be measured");

        // A SESSION-ANCHORED one does not go absent -- it re-anchors. VWAP is cumulative from the session's
        // first bar, so the raced fill anchors it at the seam and writes a number that is present, plausible,
        // and not the volume-weighted average price of anything. That is the worse of the two failures and
        // the one this repository names first: a wrong number nothing distinguishes from a right one.
        Key vwapAtTheSeam = new("vwap", 0, ConcurrencyHarness.Bucket(Seam));

        expected.Should().ContainKey(vwapAtTheSeam);
        raced.Should().ContainKey(
            vwapAtTheSeam, "an anchored indicator needs no warm-up, so the race cannot make it absent");
        raced[vwapAtTheSeam].Should().NotBe(
            expected[vwapAtTheSeam],
            "and it is anchored at the wrong bar -- the session began nineteen buckets earlier, and the "
            + "value standing in the store says it began here");

        // AND THE PLAUSIBLE WRONG NUMBER, which is the half that actually reaches a caller. Before the seam
        // the two agree exactly; after it they do not, because the smoothing was restarted.
        Before(raced, Seam).Should().Equal(
            Before(expected, Seam),
            "the earlier fill saw the start of the series and seeded from it, so its half is simply correct");

        IReadOnlyDictionary<Key, decimal> after = After(raced, Seam);
        after.Should().NotBeEmpty("the later fill did write values, or there is no wrong number to find");
        after.Should().NotEqual(
            After(expected, Seam),
            "and every one of them is smoothed from the seam rather than carried forward from the start of "
            + "the series -- a number of exactly the shape a caller cannot tell from a right one");
    }

    [Fact]
    public async Task TheNEXTPassOverTheSeries_RecomputesEveryValueTheRaceLeftStale()
    {
        // THE OTHER HALF OF THE CLAIM, and the reason gh#104 was allowed to end in "accept it". "Stale, not
        // lost" is only worth anything if something actually recomputes them, so this drives the same race and
        // then does the ordinary thing that follows it: one more fill of the same series.
        //
        // A projection is over the WHOLE stored series (ADR-0006), so a pass that writes a single further
        // bucket re-derives all of them -- the seam buckets appear, and the values after it are rewritten from
        // the start of the series. On a live instrument that is the next bar; the exposure is a backfill of
        // settled history that nothing touches again, which is what ADR-0012 records rather than hides.
        string venue = ConcurrencyHarness.Venue();
        string unraced = await FilledInOnePassAsync(0, End + 1);

        await RaceTwoAdjacentFillsAsync(venue);

        await using (TopstepXDbContext later = _fixture.CreateContext())
        {
            BarCacheService next = ConcurrencyHarness.Cache(
                later, venue, ConcurrencyHarness.Bars(End, End + 1), Now);

            BarReadResult result = await next.GetBarsAsync(
                ConcurrencyHarness.Instrument,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(End, End + 1),
                CancellationToken.None);

            result.FetchedBuckets.Should().Be(
                1,
                "the heal is a side effect of an ORDINARY fill, so this pass has to have actually written a "
                + "bucket -- a call that wrote nothing never projects and would heal nothing");
        }

        IReadOnlyDictionary<Key, decimal> healed = await ValuesAsync(venue);
        IReadOnlyDictionary<Key, decimal> expected = await ValuesAsync(unraced);

        expected.Should().NotBeEmpty("the unraced series is the yardstick, so it has to have values");
        healed.Should().Equal(
            expected,
            "one further pass reprojects the whole series from its first bar, so the raced series is now "
            + "value-for-value what it would have been had the two fills never overlapped -- which is what "
            + "ADR-0006's reproducibility buys and what makes the residue recoverable rather than permanent");
    }

    [Fact]
    public async Task RebuildIndicators_AfterTheAdjacentFillSeam_CountsTheSeriesAsRewritten()
    {
        // Option 1 (gh#348): the heal count is how many series rebuild-indicators rewrote. MES so this
        // rebuild does not walk every other test's ES series in the shared container — the verb filters by
        // instrument, not by venue (see ConcurrencyHarness.RebuildSymbol).
        const string symbol = "MES";
        InstrumentId instrument = new(symbol);
        string venue = ConcurrencyHarness.Venue();

        await RaceTwoAdjacentFillsAsync(venue, instrument);

        await using TopstepXDbContext store = _fixture.CreateContext();
        IndicatorRebuilder rebuilder = new(
            store,
            ConcurrencyHarness.Projector(store),
            new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = symbol })),
            new FakeTimeProvider(Now),
            NullLogger<IndicatorRebuilder>.Instance);

        IndicatorRebuildResult result = await rebuilder.RebuildAsync(symbol, CancellationToken.None);

        result.SeriesRewritten.Should().Be(
            1,
            "the raced series still holds the seam, so the rebuild must rewrite it — a confirming rebuild "
            + "would be 0, and that is the pin beside this one");
        result.ValuesChanged.Should().BeGreaterThan(0, "the heal changed values, not merely walked the series");

        IReadOnlyDictionary<Key, decimal> healed = await ValuesAsync(venue, symbol);
        Key atrAtTheSeam = new("atr", 3, ConcurrencyHarness.Bucket(Seam));
        healed.Should().ContainKey(
            atrAtTheSeam,
            "the increment has to be the heal — ATR at the seam was the absence the race left");
    }

    /// <summary>The identity of one stored value, without the series it belongs to.</summary>
    /// <param name="Indicator">The indicator's stable name.</param>
    /// <param name="Period">The period.</param>
    /// <param name="Bucket">The bucket.</param>
    private readonly record struct Key(string Indicator, int Period, DateTimeOffset Bucket);

    /// <summary>The part of a series' values at or after a bucket index.</summary>
    /// <param name="values">The values.</param>
    /// <param name="index">The bucket index.</param>
    /// <returns>The values at or after it.</returns>
    private static IReadOnlyDictionary<Key, decimal> After(
        IReadOnlyDictionary<Key, decimal> values, int index) =>
        values.Where(pair => pair.Key.Bucket >= ConcurrencyHarness.Bucket(index))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>The part of a series' values before a bucket index.</summary>
    /// <param name="values">The values.</param>
    /// <param name="index">The bucket index.</param>
    /// <returns>The values before it.</returns>
    private static IReadOnlyDictionary<Key, decimal> Before(
        IReadOnlyDictionary<Key, decimal> values, int index) =>
        values.Where(pair => pair.Key.Bucket < ConcurrencyHarness.Bucket(index))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>
    /// Fills one private venue's series in a single uninterrupted pass, as the yardstick.
    /// </summary>
    /// <param name="fromIndex">The first bucket index.</param>
    /// <param name="toIndexExclusive">One past the last bucket index.</param>
    /// <returns>The venue id it was filled under.</returns>
    /// <remarks>
    /// Its own venue, so it shares nothing with the raced series but the bars' numbers and the catalogue —
    /// which is precisely the comparison being made.
    /// </remarks>
    private async Task<string> FilledInOnePassAsync(int fromIndex, int toIndexExclusive)
    {
        string venue = ConcurrencyHarness.Venue();

        await using TopstepXDbContext store = _fixture.CreateContext();
        BarCacheService cache = ConcurrencyHarness.Cache(
            store, venue, ConcurrencyHarness.Bars(fromIndex, toIndexExclusive), Now);

        await cache.GetBarsAsync(
            ConcurrencyHarness.Instrument,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(fromIndex, toIndexExclusive),
            CancellationToken.None);

        return venue;
    }

    /// <summary>Drives the two adjacent fills so the later one holds a snapshot without the earlier one's bars.</summary>
    /// <param name="venue">The private venue id for this test.</param>
    /// <param name="instrument">The instrument to fill. Defaults to the harness symbol.</param>
    /// <returns>The task.</returns>
    private async Task RaceTwoAdjacentFillsAsync(string venue, InstrumentId? instrument = null)
    {
        InstrumentId id = instrument ?? ConcurrencyHarness.Instrument;

        await using TopstepXDbContext aStore = _fixture.CreateContext();

        async Task TheEarlierHalfLandsWhileThisOneHoldsItsSnapshot()
        {
            BarCacheService a = ConcurrencyHarness.Cache(
                aStore, venue, ConcurrencyHarness.Bars(0, Seam), Now);
            await a.GetBarsAsync(
                id,
                ConcurrencyHarness.ResolutionMinutes,
                ConcurrencyHarness.Window(0, Seam),
                CancellationToken.None);
        }

        InterleavingInterceptor straddle = InterleavingInterceptor.After(
            "\"Volume\"", venue, TheEarlierHalfLandsWhileThisOneHoldsItsSnapshot);

        await using TopstepXDbContext bStore = _fixture.CreateContext(straddle);
        BarCacheService b = ConcurrencyHarness.Cache(
            bStore, venue, ConcurrencyHarness.Bars(Seam, End), Now);

        await b.GetBarsAsync(
            id,
            ConcurrencyHarness.ResolutionMinutes,
            ConcurrencyHarness.Window(Seam, End),
            CancellationToken.None);

        straddle.Fired.Should().BeTrue(
            "the interleaving is what makes this a race -- without it the heal below would be healing nothing");
    }

    /// <summary>Every stored value for a series, keyed by what identifies it inside that series.</summary>
    /// <param name="venue">The venue id.</param>
    /// <param name="symbol">The instrument symbol. Defaults to the harness symbol.</param>
    /// <returns>The values.</returns>
    /// <remarks>
    /// <c>RecordedAt</c> is deliberately not read. It moves whenever a value is rewritten, so comparing it
    /// across two venues would report the write history rather than the numbers.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Key, decimal>> ValuesAsync(string venue, string? symbol = null)
    {
        string instrument = symbol ?? ConcurrencyHarness.Symbol;
        await using TopstepXDbContext reader = _fixture.CreateContext();

        return await reader.IndicatorValues
            .AsNoTracking()
            .Where(v => v.Venue == venue
                && v.Instrument == instrument
                && v.ResolutionMinutes == ConcurrencyHarness.ResolutionMinutes)
            .ToDictionaryAsync(
                v => new Key(v.Indicator, v.Period, v.BucketStart),
                v => v.Value);
    }
}
