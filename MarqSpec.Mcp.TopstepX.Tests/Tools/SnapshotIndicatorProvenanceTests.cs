using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// Where each number in a snapshot's <c>indicators</c> map actually came from (gh#286).
/// </summary>
/// <remarks>
/// <para>
/// <b>One anchor is not one provenance, and that is the whole finding.</b> A slice reads every indicator as of
/// a single moment — <c>series.Bars[^1].T</c>, or the clock when there are no bars — so it is tempting to
/// conclude that one as-of describes the whole map. It does not. The anchor is where the read <i>stopped</i>;
/// the bucket is where the value was <i>computed</i>, and an as-of read takes the last row at or before the
/// anchor. Warm-up restarts at every contract seam (<c>R-2.7</c>,
/// <see cref="MarqSpec.Mcp.TopstepX.MarketData.IndicatorProjector"/>), so immediately after a roll the
/// indicators whose period the new contract's bars do not yet satisfy fall back to a row on the <i>expiring</i>
/// contract, while the ones that can measure sit on the bar in front. Both arrive in the same map.
/// </para>
/// <para>
/// The fixture below is the measurement, not an illustration: nine bars, six on the expiring quarter and three
/// on the new front. At the anchor, <c>atr</c> is the expiring contract's number from three buckets back and
/// <c>vwap</c> is the new contract's from the last bar. Under the old shape the payload read
/// <c>{ "atr": 2, "vwap": 140 }</c> — fifteen minutes and one contract apart, with nothing saying so, and
/// <c>atr: 2</c> is <b>half</b> the range the contract in front is actually trading.
/// </para>
/// <para>
/// So the reading, not the number, is what travels: the same <see cref="ToolPayloads.IndicatorReading"/>
/// <c>get_indicator_at</c> returns. <b>Cannot-measure is unchanged</b> — the map's own <c>null</c>, because the
/// serializer's ignore condition does not reach inside a dictionary and the catalogue tells callers to test
/// exactly that. <c>PayloadNullWireShapeTests</c> pins that half against the real options.
/// </para>
/// </remarks>
public sealed class SnapshotIndicatorProvenanceTests : IDisposable
{
    private const string Expiring = "CON.F.US.EP.U26";
    private const string NewFront = "CON.F.US.EP.Z26";

    /// <summary>The bar index the new front contract starts at.</summary>
    /// <remarks>
    /// Six bars then three, deliberately unequal. Equal runs would let every indicator warm up on both sides
    /// or on neither, and the map would agree with itself by construction — measuring nothing about
    /// provenance. Three bars is one short of ATR(3)'s <c>period + 1</c> seed and two short of SMA(5)'s
    /// window, so the new contract can carry the session-anchored VWAP and nothing else.
    /// </remarks>
    private const int RollAt = 6;

    private const int TotalBars = 9;

    /// <summary>ATR(3) over the expiring run, hand-checked.</summary>
    /// <remarks>
    /// Those six bars are flat at 100 with a high-low range of exactly 2, so every true range is that same 2 —
    /// <c>H-L</c> is 2 and both gaps to the previous close are 1 — the Wilder seed is its own mean, and every
    /// value after it repeats. Exact in <c>decimal</c>.
    /// </remarks>
    private const decimal ExpiringAtr = 2m;

    /// <summary>VWAP over the new front's run, hand-checked.</summary>
    /// <remarks>
    /// Its three bars are flat at 140 with a range of 4, so each typical price is 140 and any volume weighting
    /// of a constant is that constant. The new contract's own bar range is 4 — <b>twice</b>
    /// <see cref="ExpiringAtr"/>, which is the harm: the snapshot reports the smaller number as the market's.
    /// </remarks>
    private const decimal NewFrontVwap = 140m;

    private readonly TopstepXDbContext _database;

    public SnapshotIndicatorProvenanceTests() =>
        _database = new TopstepXDbContext(
            new DbContextOptionsBuilder<TopstepXDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                    .InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    public void Dispose() => _database.Dispose();

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    private static DateTimeOffset Bucket(int index) => SessionStart.AddMinutes(5 * index);

    [Fact]
    public async Task TwoReadingsInOneSlice_CanComeFromDifferentBucketsAndDifferentContracts()
    {
        SnapshotTools snapshot = await ComposeAsync();

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [5], TotalBars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Bars.Should().HaveCount(
            TotalBars, "the window has to hold both runs, or there is no seam inside this slice to measure");
        slice.Contracts.Span.Should().Be(ToolPayloads.ContractSpan.SpansRoll);

        slice.Indicators["atr"].Should().NotBeNull(
            "ATR(3) cannot measure on the new front's three bars, so the as-of read falls back to the "
            + "expiring run rather than returning nothing");

        slice.Indicators["vwap"].Should().NotBeNull(
            "VWAP is session-anchored rather than windowed, so it measures from the new front's first bar");

        ToolPayloads.IndicatorReading atr = slice.Indicators["atr"]!;
        ToolPayloads.IndicatorReading vwap = slice.Indicators["vwap"]!;

        atr.Value.Should().Be(ExpiringAtr);
        atr.BucketStart.Should().Be(Bucket(RollAt - 1), "the last bar of the expiring run is the newest "
            + "bucket at or before the anchor that ATR(3) has a row for");
        atr.ContractId.Should().Be(Expiring);

        vwap.Value.Should().Be(NewFrontVwap);
        vwap.BucketStart.Should().Be(Bucket(TotalBars - 1), "the anchor itself, which VWAP does have a row for");
        vwap.ContractId.Should().Be(NewFront);

        // The statement of the defect, and the reason a single slice-wide as-of would not have carried it: one
        // anchor produced two readings fifteen minutes and one contract apart.
        atr.BucketStart.Should().NotBe(vwap.BucketStart);
        atr.ContractId.Should().NotBe(vwap.ContractId);
    }

    [Fact]
    public async Task AnIndicatorThatCannotMeasure_KeepsItsKey_AndItsValueIsStillTheMapsOwnNull()
    {
        // The half that does NOT change, measured in the same fixture as the half that does -- so a shape
        // change that quietly turned cannot-measure into an empty object goes red here rather than being
        // discovered by a caller whose `indicators.x === null` test silently stopped matching.
        SnapshotTools snapshot = await ComposeAsync();

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [5], TotalBars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Indicators.Should().ContainKey(
            "bb-middle",
            "every indicator this server computes is assigned a key unconditionally, so presence still says "
            + "nothing about measurability");

        slice.Indicators["bb-middle"].Should().BeNull(
            "the Bollinger window of 20 is not satisfied by either run, and a missing number is missing");
    }

    [Fact]
    public async Task ABarLessSlice_SaysHowFarBehindTheAnchorItsReadingWasComputed()
    {
        // The state gh#268 pinned and gh#286 is about: an instrument that stopped updating. The look-back
        // window reaches back from a clock thirty days past the fixture and finds no bars, while the stored
        // indicator rows sit behind it -- so the payload carried a month-old number that read as current, and
        // the slice's own `contracts` block, describing zero bars, could not contradict it.
        SnapshotTools snapshot = await ComposeAsync(now: Bucket(TotalBars - 1).AddDays(30));

        ToolPayloads.MarketSnapshot payload =
            await snapshot.GetMarketSnapshot("ES", [5], TotalBars, CancellationToken.None);

        ToolPayloads.ResolutionSnapshot slice = payload.PerResolution.Should().ContainSingle().Subject;

        slice.Bars.Should().BeEmpty("this is the zero-bars branch -- a slice with bars is the other one");
        slice.Contracts.Segments.Should().BeEmpty("no bars means no coverage to check a reading against");

        slice.Indicators["vwap"].Should().NotBeNull(
            "the stored rows are still there; only the bar window moved past them");

        ToolPayloads.IndicatorReading vwap = slice.Indicators["vwap"]!;

        vwap.BucketStart.Should().Be(
            Bucket(TotalBars - 1),
            "and now the caller can see it: the number is the last stored bucket's, thirty days behind the "
            + "moment it was asked for");

        vwap.ContractId.Should().Be(
            NewFront,
            "the contract is carried too, which is the only place it appears in this payload at all -- the "
            + "slice's own coverage describes zero bars");
    }

    /// <summary>
    /// Seeds nine bars across a roll, projects the indicators over them, and composes the snapshot.
    /// </summary>
    /// <param name="now">
    /// The moment every part of the composition agrees is <i>now</i>. Defaults to two hours past the last
    /// bucket, which puts the look-back window over the whole fixture.
    /// </param>
    /// <returns>The snapshot tool.</returns>
    /// <remarks>
    /// One <see cref="FakeTimeProvider"/> is shared by the cache, the market-data tool, the reference tool and
    /// the snapshot, because that is how the container wires it — a test that handed the snapshot a clock of
    /// its own could pass with the composition root still giving the real one to everything else.
    /// </remarks>
    private async Task<SnapshotTools> ComposeAsync(DateTimeOffset? now = null)
    {
        for (int i = 0; i < TotalBars; i++)
        {
            bool rolled = i >= RollAt;
            decimal close = rolled ? 140m : 100m;
            decimal halfRange = rolled ? 2m : 1m;

            _database.Bars.Add(new BarRecord
            {
                Venue = "test",
                Instrument = "ES",
                ResolutionMinutes = 5,
                BucketStart = Bucket(i),
                Open = close,
                High = close + halfRange,
                Low = close - halfRange,
                Close = close,
                Volume = 1_000,
                ContractId = rolled ? NewFront : Expiring,
                RecordedAt = SessionStart,
            });
        }

        await _database.SaveChangesAsync();

        MarketDataOptions options = new()
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        };
        IOptions<MarketDataOptions> wrapped = Options.Create(options);

        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);

        // Short periods so the runs either side of the seam are long enough to be hand-checked and short
        // enough that one satisfies a period the other does not. The shipped 14 and 20 would leave every
        // windowed indicator absent on both runs, and a map of nulls measures nothing about provenance.
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3, SmaPeriod = 5, EmaPeriod = 5 }),
            calendar);

        FakeTimeProvider clock = new(now ?? Bucket(TotalBars).AddHours(2));

        // Serves nothing, so the window each case reads is filled from the store alone. A gateway holding bars
        // would fill the bar-less case's window and take it off the branch under test.
        CountingGateway gateway = new([]);

        IndicatorProjector projector = new(_database, catalog, NullLogger<IndicatorProjector>.Instance);
        await projector.ProjectAsync("test", new InstrumentId("ES"), 5, SessionStart, CancellationToken.None);
        await _database.SaveChangesAsync();

        BarCacheService cache = new(
            _database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        MarketDataTools marketData = new(
            cache,
            _database,
            new InstrumentRegistry(wrapped),
            catalog,
            new IndicatorCacheService(
                _database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(wrapped),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(new KeyLevelDetectionOptions()));

        ReferenceTools reference = new(
            new InstrumentRegistry(wrapped), calendar, gateway, wrapped, clock);

        return new SnapshotTools(marketData, reference, new IndicatorCatalogNames(catalog), clock);
    }
}
