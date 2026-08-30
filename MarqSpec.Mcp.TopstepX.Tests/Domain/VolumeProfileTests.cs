using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Point of control and 70% value area from a fixed cell set, with every number added by hand.
/// </summary>
/// <remarks>
/// <para>
/// A volume profile is an aggregate over <see cref="FootprintCell"/>s, not a stored table (gh#221).
/// The sums and the 70% threshold below are what a person with a pencil would write; a test that
/// asserts the code does what the code does would pass forever and prove nothing.
/// </para>
/// <para>
/// The 70% definition is pinned here so two implementations cannot both look right and disagree:
/// ties at the POC, and how a two-level expansion terminates when one side has an odd leftover
/// price. The same sentences live on <see cref="VolumeProfileAggregator"/>.
/// </para>
/// </remarks>
public sealed class VolumeProfileTests
{
    private const string Es = "ES";
    private const int FiveMinutes = 5;

    private static readonly DateTimeOffset _bucket1430 = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _bucket1435 = new(2026, 8, 18, 14, 35, 0, TimeSpan.Zero);

    [Fact]
    public void Poc_IsThePriceWithTheMostVolume()
    {
        // 14:30:  4999.75 × (2+1) = 3
        //         5000.00 × (8+2) = 10
        //         5000.25 × (4+0) = 4
        // 10 is the most. Total = 17. 70% of 17 is 11.9, so the value area is just the POC:
        // 10 is short of 11.9, then the next step adds 4 above (or 3 below) and crosses.
        // Above pair: 5000.25 only = 4. Below pair: 4999.75 only = 3. Add above.
        // VA volume = 14, low = 5000.00, high = 5000.25.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 4999.75m, buy: 2, sell: 1),
            Cell(_bucket1430, 5000.00m, buy: 8, sell: 2),
            Cell(_bucket1430, 5000.25m, buy: 4, sell: 0),
        ]);

        profile.PointOfControl.Should().Be(5000.00m);
        profile.TotalVolume.Should().Be(17);
        profile.ValueAreaLow.Should().Be(5000.00m);
        profile.ValueAreaHigh.Should().Be(5000.25m);
        profile.ValueAreaVolume.Should().Be(14);
        profile.ByPrice.Should().Equal(
            new VolumeAtPrice(4999.75m, 3),
            new VolumeAtPrice(5000.00m, 10),
            new VolumeAtPrice(5000.25m, 4));
    }

    [Fact]
    public void VolumeAtOnePrice_SumsAcrossBuckets()
    {
        // Same price in two bars: (3+2) + (4+1) = 10 at 5000. One bar at 5000.25: 3.
        // Total = 13. 70% of 13 is 9.1. The POC alone is 10, so the value area is just that price.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 5000.00m, buy: 3, sell: 2),
            Cell(_bucket1435, 5000.00m, buy: 4, sell: 1),
            Cell(_bucket1430, 5000.25m, buy: 0, sell: 3),
        ]);

        profile.PointOfControl.Should().Be(5000.00m);
        profile.TotalVolume.Should().Be(13);
        profile.ValueAreaLow.Should().Be(5000.00m);
        profile.ValueAreaHigh.Should().Be(5000.00m);
        profile.ValueAreaVolume.Should().Be(10);
        profile.ByPrice.Should().Equal(
            new VolumeAtPrice(5000.00m, 10),
            new VolumeAtPrice(5000.25m, 3));
    }

    [Fact]
    public void Poc_OnAVolumeTie_TakesThePriceClosestToTheTradedMidpoint()
    {
        // 20 at 4999.00, 20 at 4999.25, 10 at 4999.50. Highest volume is a tie.
        // Traded range midpoint is (4999.00 + 4999.50) / 2 = 4999.25.
        // Distances: 4999.00 → 0.25, 4999.25 → 0. The closer price is 4999.25.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 4999.00m, buy: 20, sell: 0),
            Cell(_bucket1430, 4999.25m, buy: 10, sell: 10),
            Cell(_bucket1430, 4999.50m, buy: 10, sell: 0),
        ]);

        profile.PointOfControl.Should().Be(4999.25m);
    }

    [Fact]
    public void Poc_OnATieEquidistantFromTheMidpoint_TakesTheLowerPrice()
    {
        // 20 at 4998, 10 at 4999, 20 at 5000. Midpoint is (4998 + 5000) / 2 = 4999.
        // Both 4998 and 5000 sit 1 away. The lower price wins, so two implementations
        // handed the cells in opposite order cannot disagree.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 5000m, buy: 20, sell: 0),
            Cell(_bucket1430, 4999m, buy: 10, sell: 0),
            Cell(_bucket1430, 4998m, buy: 20, sell: 0),
        ]);

        profile.PointOfControl.Should().Be(4998m);
    }

    [Fact]
    public void ValueArea_AddsTheWholeWinningPair_EvenWhenThatCrossesSeventyPercent()
    {
        // 50 at 5000, 20 at 5001, 20 at 5002, 5 at 4999. Total = 95. 70% of 95 is 66.5.
        // POC is 5000 (50). Need 16.5 more.
        // Next two above: 20 + 20 = 40. Next one below: 5.
        // The pair wins. Adding only 5001 would land at 70, closer to 70% — that is not the rule.
        // The whole winning group is added, even when that overshoots.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 4999m, buy: 5, sell: 0),
            Cell(_bucket1430, 5000m, buy: 50, sell: 0),
            Cell(_bucket1430, 5001m, buy: 20, sell: 0),
            Cell(_bucket1430, 5002m, buy: 20, sell: 0),
        ]);

        profile.PointOfControl.Should().Be(5000m);
        profile.TotalVolume.Should().Be(95);
        profile.ValueAreaLow.Should().Be(5000m);
        profile.ValueAreaHigh.Should().Be(5002m);
        profile.ValueAreaVolume.Should().Be(90);
    }

    [Fact]
    public void ValueArea_OnAnOddLeftover_ComparesOneLevelAgainstTwo()
    {
        // One price below the POC, two above — the even/odd expansion-boundary.
        // 25 at 4999, 30 at 5000, 5 at 5001, 5 at 5002. Total = 65. 70% of 65 is 45.5.
        // POC is 5000 (30). Need 15.5 more.
        // Next two above: 5 + 5 = 10. Next one below: 25.
        // The single leftover wins, so expansion adds one level, not two.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 4999m, buy: 25, sell: 0),
            Cell(_bucket1430, 5000m, buy: 30, sell: 0),
            Cell(_bucket1430, 5001m, buy: 5, sell: 0),
            Cell(_bucket1430, 5002m, buy: 5, sell: 0),
        ]);

        profile.PointOfControl.Should().Be(5000m);
        profile.TotalVolume.Should().Be(65);
        profile.ValueAreaLow.Should().Be(4999m);
        profile.ValueAreaHigh.Should().Be(5000m);
        profile.ValueAreaVolume.Should().Be(55);
    }

    [Fact]
    public void ValueArea_OnAVolumeTieBetweenSides_AddsTheLowerSide()
    {
        // 10 at 4999, 20 at 5000, 10 at 5001. Total = 40. 70% of 40 is 28.
        // POC is 5000 (20). Need 8 more. Both sides have 10.
        // A tie expands downward, so the answer does not depend on which side is inspected first.
        VolumeProfile profile = VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 4999m, buy: 10, sell: 0),
            Cell(_bucket1430, 5000m, buy: 20, sell: 0),
            Cell(_bucket1430, 5001m, buy: 10, sell: 0),
        ]);

        profile.PointOfControl.Should().Be(5000m);
        profile.ValueAreaLow.Should().Be(4999m);
        profile.ValueAreaHigh.Should().Be(5000m);
        profile.ValueAreaVolume.Should().Be(30);
    }

    [Fact]
    public void AnEmptyCellSet_Refuses_RatherThanReturningAnEmptyProfile()
    {
        Action compute = () => VolumeProfileAggregator.From([]);

        compute.Should().Throw<ArgumentException>().WithMessage("*volume*");
    }

    [Fact]
    public void CellsThatCarryNoVolume_Refuse_RatherThanInventingAPoc()
    {
        Action compute = () => VolumeProfileAggregator.From(
        [
            Cell(_bucket1430, 5000m, buy: 0, sell: 0),
        ]);

        compute.Should().Throw<ArgumentException>().WithMessage("*volume*");
    }

    [Fact]
    public void CellsFromTwoInstruments_AreRefused()
    {
        Action compute = () => VolumeProfileAggregator.From(
        [
            new FootprintCell(Es, FiveMinutes, _bucket1430, 5000m, 2, 0),
            new FootprintCell("NQ", FiveMinutes, _bucket1430, 18000m, 2, 0),
        ]);

        compute.Should().Throw<ArgumentException>().WithMessage("*instrument*");
    }

    private static FootprintCell Cell(
        DateTimeOffset bucket,
        decimal price,
        long buy,
        long sell) =>
        new(Es, FiveMinutes, bucket, price, buy, sell);
}
