using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Prints into footprint cells, with numbers added by hand rather than captured from the aggregator.
/// </summary>
/// <remarks>
/// <para>
/// A footprint is buy and sell volume per price per bar. The sums below are what a person with a pencil
/// would write; a test that asserts the code does what the code does would pass forever and prove nothing
/// (gh#220).
/// </para>
/// <para>
/// <b>The trap this issue exists to survive:</b> the vendor's <c>TradeLogType.Buy = 0</c> and has no
/// Unknown, so an unstated direction deserialises silently to a buy. <see cref="TradeDirection.Unknown"/>
/// is stored as zero on purpose. Counting it as volume would report a number that looks like a real cell.
/// </para>
/// </remarks>
public sealed class FootprintAggregatorTests
{
    private const string Es = "ES";
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";
    private const int FiveMinutes = 5;

    /// <summary>The 14:30 UTC 5-minute bucket — already on the .NET-epoch grid.</summary>
    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

    /// <summary>The next 5-minute bucket.</summary>
    private static readonly DateTimeOffset _bucket1435 = new(2026, 8, 18, 14, 35, 0, TimeSpan.Zero);

    [Fact]
    public void UnknownIsZero_AndBuyIsNot()
    {
        // The closed vocabulary, pinned where Domain owns it. If Unknown ever moves off zero, an absent
        // vendor direction can silently become a buy again the moment someone maps an int through.
        ((int)TradeDirection.Unknown).Should().Be(0);
        ((int)TradeDirection.Buy).Should().Be(1);
        ((int)TradeDirection.Sell).Should().Be(2);
    }

    [Fact]
    public void ThreePrints_ProduceTwoCells_WithHandCheckedVolumes()
    {
        // Inside the 14:30 bar:
        //   14:31:00  5000.00  Buy   2
        //   14:32:00  5000.00  Buy   3
        //   14:33:00  5000.25  Sell  4
        // On the next boundary:
        //   14:35:00  5000.00  Buy   1
        //
        // 2 + 3 = 5 buy at 5000 in 14:30. 4 sell at 5000.25 in 14:30. 1 buy at 5000 in 14:35.
        IReadOnlyList<FootprintCell> cells = FootprintAggregator.Aggregate(
            [
                Print(new DateTimeOffset(2026, 8, 18, 14, 31, 0, TimeSpan.Zero), 5000.00m, 2, TradeDirection.Buy),
                Print(new DateTimeOffset(2026, 8, 18, 14, 32, 0, TimeSpan.Zero), 5000.00m, 3, TradeDirection.Buy),
                Print(new DateTimeOffset(2026, 8, 18, 14, 33, 0, TimeSpan.Zero), 5000.25m, 4, TradeDirection.Sell),
                Print(new DateTimeOffset(2026, 8, 18, 14, 35, 0, TimeSpan.Zero), 5000.00m, 1, TradeDirection.Buy),
            ],
            FiveMinutes);

        cells.Should().BeEquivalentTo(
        [
            new FootprintCell(Es, FiveMinutes, _bucket1430, 5000.00m, BuyVolume: 5, SellVolume: 0),
            new FootprintCell(Es, FiveMinutes, _bucket1430, 5000.25m, BuyVolume: 0, SellVolume: 4),
            new FootprintCell(Es, FiveMinutes, _bucket1435, 5000.00m, BuyVolume: 1, SellVolume: 0),
        ]);
    }

    [Fact]
    public void AnUnknownPrint_DoesNotMoveTheCell()
    {
        // Same 14:30 / 5000 buys as above (2 + 3 = 5), plus an Unknown of 100 at the same price.
        // If Unknown were counted as Buy — TradeLogType.Buy = 0 — the cell would read 105.
        IReadOnlyList<FootprintCell> cells = FootprintAggregator.Aggregate(
            [
                Print(new DateTimeOffset(2026, 8, 18, 14, 31, 0, TimeSpan.Zero), 5000m, 2, TradeDirection.Buy),
                Print(new DateTimeOffset(2026, 8, 18, 14, 32, 0, TimeSpan.Zero), 5000m, 3, TradeDirection.Buy),
                Print(new DateTimeOffset(2026, 8, 18, 14, 32, 30, TimeSpan.Zero), 5000m, 100, TradeDirection.Unknown),
            ],
            FiveMinutes);

        cells.Should().ContainSingle()
            .Which.Should().Be(new FootprintCell(Es, FiveMinutes, _bucket1430, 5000m, BuyVolume: 5, SellVolume: 0));
    }

    [Fact]
    public void APrintThatIsOnlyUnknown_ProducesNoCell()
    {
        // A cell of 0/0 at that price would look like a market that traded and netted nothing.
        // An unstated direction is an absence, not a flat print.
        IReadOnlyList<FootprintCell> cells = FootprintAggregator.Aggregate(
            [
                Print(new DateTimeOffset(2026, 8, 18, 14, 31, 0, TimeSpan.Zero), 5000m, 7, TradeDirection.Unknown),
            ],
            FiveMinutes);

        cells.Should().BeEmpty();
    }

    [Fact]
    public void BucketsAlignWithTheBarGrid_NotASecondEpoch()
    {
        // Bars store OpenTime as BucketStart. A print at 14:34:59 belongs to the bar that opens at 14:30;
        // a print at 14:35:00 belongs to the next bar. AlignDown is the existing .NET-epoch grid
        // (BarGapDetector) — inventing another one would put footprint cells on a different window
        // than the price bar they claim to describe.
        DateTimeOffset justBefore = new(2026, 8, 18, 14, 34, 59, TimeSpan.Zero);
        DateTimeOffset onBoundary = new(2026, 8, 18, 14, 35, 0, TimeSpan.Zero);

        BarGapDetector.AlignDown(justBefore, TimeSpan.FromMinutes(FiveMinutes)).Should().Be(_bucket1430);
        BarGapDetector.AlignDown(onBoundary, TimeSpan.FromMinutes(FiveMinutes)).Should().Be(_bucket1435);

        IReadOnlyList<FootprintCell> cells = FootprintAggregator.Aggregate(
            [
                Print(justBefore, 5000m, 2, TradeDirection.Buy),
                Print(onBoundary, 5000m, 3, TradeDirection.Sell),
            ],
            FiveMinutes);

        cells.Should().BeEquivalentTo(
        [
            new FootprintCell(Es, FiveMinutes, _bucket1430, 5000m, BuyVolume: 2, SellVolume: 0),
            new FootprintCell(Es, FiveMinutes, _bucket1435, 5000m, BuyVolume: 0, SellVolume: 3),
        ]);
    }

    [Fact]
    public void AnEmptyTape_YieldsNoCells()
    {
        FootprintAggregator.Aggregate([], FiveMinutes).Should().BeEmpty();
    }

    [Fact]
    public void PrintsFromTwoContractsInTheSameBucket_ProduceNoCell()
    {
        // 10 buy at 100 on the expiring contract and 5 sell at 140 on the new one, same 5-minute window.
        // Adding them — or keeping each price — would report the roll gap as the bar's footprint.
        // The whole bucket is refused rather than fabricated.
        IReadOnlyList<FootprintCell> cells = FootprintAggregator.Aggregate(
            [
                Print(new DateTimeOffset(2026, 8, 18, 14, 31, 0, TimeSpan.Zero), 100m, 10, TradeDirection.Buy, Front),
                Print(new DateTimeOffset(2026, 8, 18, 14, 32, 0, TimeSpan.Zero), 140m, 5, TradeDirection.Sell, Next),
            ],
            FiveMinutes);

        cells.Should().BeEmpty();
    }

    [Fact]
    public void AdjacentContractRuns_InDifferentBuckets_EachKeepTheirOwnCell()
    {
        // The clean roll: last print of the front month in 14:30, first of the next in 14:35.
        // Each run is a real bar; refusing the bucket would drop two honest cells.
        IReadOnlyList<FootprintCell> cells = FootprintAggregator.Aggregate(
            [
                Print(new DateTimeOffset(2026, 8, 18, 14, 31, 0, TimeSpan.Zero), 100m, 8, TradeDirection.Buy, Front),
                Print(new DateTimeOffset(2026, 8, 18, 14, 36, 0, TimeSpan.Zero), 140m, 6, TradeDirection.Sell, Next),
            ],
            FiveMinutes);

        cells.Should().BeEquivalentTo(
        [
            new FootprintCell(Es, FiveMinutes, _bucket1430, 100m, BuyVolume: 8, SellVolume: 0),
            new FootprintCell(Es, FiveMinutes, _bucket1435, 140m, BuyVolume: 0, SellVolume: 6),
        ]);
    }

    private static TradePrint Print(
        DateTimeOffset when,
        decimal price,
        long size,
        TradeDirection direction,
        string contractId = Front) =>
        new(Es, contractId, when, price, size, direction);
}
