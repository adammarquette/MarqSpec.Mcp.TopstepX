using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The seam a quarterly roll leaves in a symbol-keyed series, and the refusal to compute across it.
/// </summary>
/// <remarks>
/// <para>
/// Bars are keyed by the venue-neutral symbol (<c>ES</c>) and contract resolution picks the front month, so
/// when the front month rolls the next fetch stores a <i>different contract's</i> bars under the same key,
/// directly beside the old one's. Adjacent ES quarters do not trade at the same price — the gap is routinely
/// tens of points — and nothing in the series marks where one ends and the next begins (gh#42).
/// </para>
/// <para>
/// The numbers below are the whole argument. Over the spliced series the ATR(3) at the first bar of the new
/// contract is <c>46/3 ≈ 15.33</c>; over the new contract alone it is <b>4</b>. Nothing errors, nothing looks
/// odd, and a volatility reading nearly four times the truth is a bookkeeping event wearing a market's
/// clothes.
/// </para>
/// </remarks>
public sealed class ContractRollTests
{
    private const string Front = "CON.F.US.EP.U26";
    private const string Next = "CON.F.US.EP.Z26";

    private static DateTimeOffset SessionStart =>
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 0)).ToUniversalTime();

    /// <summary>Four bars around 100 on the expiring contract, then four around 140 on the new one.</summary>
    /// <remarks>
    /// The two halves have deliberately <b>different ranges</b> — 2 points and 4 points — so the ATR of one
    /// cannot be mistaken for the ATR of the other in an assertion.
    /// </remarks>
    private static IReadOnlyList<Bar> Spliced() => [.. Front4(), .. Next4()];

    private static IReadOnlyList<Bar> Front4() =>
        [.. Enumerable.Range(0, 4).Select(i => new Bar(
            SessionStart.AddMinutes(5 * i), 100m, 101m, 99m, 100m, 1_000, Front))];

    private static IReadOnlyList<Bar> Next4() =>
        [.. Enumerable.Range(4, 4).Select(i => new Bar(
            SessionStart.AddMinutes(5 * i), 140m, 142m, 138m, 140m, 1_000, Next))];

    // ── Segmentation ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASingleContractSeries_IsOneSegment_AndSpansNoRoll()
    {
        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(Front4());

        segments.Should().ContainSingle();
        segments[0].ContractId.Should().Be(Front);
        segments[0].BarCount.Should().Be(4);
        ContractRollDetector.SpansRoll(Front4()).Should().BeFalse();
    }

    [Fact]
    public void TwoContracts_AreTwoSegments_MeetingAtTheSeam()
    {
        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(Spliced());

        segments.Should().HaveCount(2);
        segments[0].ContractId.Should().Be(Front);
        segments[0].FirstBucket.Should().Be(SessionStart);
        segments[0].LastBucket.Should().Be(SessionStart.AddMinutes(15));
        segments[1].ContractId.Should().Be(Next);
        segments[1].FirstBucket.Should().Be(SessionStart.AddMinutes(20));
        segments[1].StartIndex.Should().Be(4);
        ContractRollDetector.SpansRoll(Spliced()).Should().BeTrue();
    }

    [Fact]
    public void BarsWithNoRecordedContract_AreOneSegmentOfUnknownProvenance()
    {
        // Every row written before the contract id was stored has none, and it was never captured, so it
        // cannot be recovered. "Unknown" is one value, not many: a store full of them is still a single
        // segment, and the indicators over it keep working exactly as they did.
        IReadOnlyList<Bar> unrecorded =
            [.. Enumerable.Range(0, 4).Select(i => new Bar(
                SessionStart.AddMinutes(5 * i), 100m, 101m, 99m, 100m, 1_000))];

        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(unrecorded);

        segments.Should().ContainSingle();
        segments[0].ContractId.Should().BeNull();
        ContractRollDetector.SpansRoll(unrecorded).Should().BeFalse();
    }

    [Fact]
    public void AnUnrecordedRunNextToAKnownContract_IsASeam()
    {
        // The state every existing store is in the moment the column arrives: old rows carry no contract,
        // new ones do. They are NOT known to be the same contract, and treating them as one series is the
        // assumption this whole record exists to refuse.
        IReadOnlyList<Bar> mixed =
        [
            new Bar(SessionStart, 100m, 101m, 99m, 100m, 1_000),
            new Bar(SessionStart.AddMinutes(5), 100m, 101m, 99m, 100m, 1_000, Next),
        ];

        ContractRollDetector.Segment(mixed).Should().HaveCount(2);
        ContractRollDetector.SpansRoll(mixed).Should().BeTrue();
    }

    [Fact]
    public void AnEmptySeries_HasNoSegments_AndSpansNoRoll()
    {
        ContractRollDetector.Segment([]).Should().BeEmpty();
        ContractRollDetector.SpansRoll([]).Should().BeFalse();
    }

    [Fact]
    public void AContractThatReappearsLater_IsThreeSegments()
    {
        // Interleaving is worse than a clean splice, not better: it is what a backfill under the wrong front
        // month looks like. Contiguous runs report it as three seams rather than folding it back to two
        // contracts and hiding the disorder.
        IReadOnlyList<Bar> interleaved =
        [
            new Bar(SessionStart, 100m, 101m, 99m, 100m, 1_000, Front),
            new Bar(SessionStart.AddMinutes(5), 140m, 142m, 138m, 140m, 1_000, Next),
            new Bar(SessionStart.AddMinutes(10), 100m, 101m, 99m, 100m, 1_000, Front),
        ];

        ContractRollDetector.Segment(interleaved).Should().HaveCount(3);
    }

    // ── The refusal ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Atr_IsNotComputedAcrossASplice()
    {
        // THE acceptance test for gh#42. True range at the seam is max(142-138, |142-100|, |138-100|) = 42,
        // so a spliced ATR(3) reads 46/3 ≈ 15.33 where the new contract's own volatility is 4. It does not
        // error and it does not look wrong; it is simply the roll gap being reported as market movement.
        Action compute = () => AverageTrueRange.Compute(Spliced(), 3);

        compute.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void Atr_OverTheNewContractAlone_MeasuresTheNewContract()
    {
        // Hand-checked, not captured. Every bar of the new contract has H-L = 4 and closes at 140, so every
        // true range after the first is max(4, 2, 2) = 4. The Wilder seed at index 3 is the mean of the first
        // three, (4+4+4)/3 = 4 — exact in decimal, with no rounding to hide a drift behind.
        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(Next4(), 3);

        atr.Should().HaveCount(4);
        atr[0].Should().BeNull();
        atr[1].Should().BeNull();
        atr[2].Should().BeNull();
        atr[3].Should().Be(4m);
    }

    [Fact]
    public void Atr_OverTheExpiringContractAlone_MeasuresThatOneInstead()
    {
        // The same arithmetic on the other side of the seam: H-L = 2 throughout, so the seed is (2+2+2)/3 = 2.
        IReadOnlyList<decimal?> atr = AverageTrueRange.Compute(Front4(), 3);

        atr[3].Should().Be(2m);
    }

    [Fact]
    public void Rsi_IsNotComputedAcrossASplice()
    {
        Action compute = () => RelativeStrengthIndex.Compute(Spliced(), 3);

        compute.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void MovingAverages_AreNotComputedAcrossASplice()
    {
        Action simple = () => MovingAverages.Simple(Spliced(), 3);
        Action exponential = () => MovingAverages.Exponential(Spliced(), 3);

        simple.Should().Throw<ArgumentException>().WithMessage("*contract*");
        exponential.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void BollingerBands_AreNotComputedAcrossASplice()
    {
        Action middle = () => BollingerBands.Middle(Spliced(), 3);
        Action deviation = () => BollingerBands.StandardDeviation(Spliced(), 3);

        middle.Should().Throw<ArgumentException>().WithMessage("*contract*");
        deviation.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void Macd_IsNotComputedAcrossASplice()
    {
        Action line = () => Macd.Line(Spliced(), 13);

        line.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void Vwap_IsNotComputedAcrossASplice()
    {
        Action compute = () =>
            VolumeWeightedAveragePrice.Compute(Spliced(), BarSessionCalendar.Parse("16:00", []));

        compute.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void KeyLevels_AreNotDetectedAcrossASplice()
    {
        // The harm here is the one an agent acts on directly: a level "formed" at 100 while the contract
        // in front of it has never traded below 138.
        IReadOnlyList<decimal?> atr = [.. Enumerable.Repeat<decimal?>(2m, 8)];
        Action detect = () => KeyLevels.Detect(Spliced(), atr, new KeyLevelOptions());

        detect.Should().Throw<ArgumentException>().WithMessage("*contract*");
    }

    [Fact]
    public void ASingleContractSeries_IsStillComputed()
    {
        // The guard must refuse the splice and nothing else. A check that also refused ordinary series would
        // be found immediately; one that refused nothing would not be found at all.
        Action compute = () => AverageTrueRange.Compute(Front4(), 3);

        compute.Should().NotThrow();
    }
}
