using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The five published formulas, as arithmetic — no bars, no calendar, no zones.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="PivotLevels.Lines"/> is separate from <see cref="PivotLevels.Compute"/> so that this file
/// can exist</b>, and the claim is only worth making if something takes it up. Every case below hands the
/// same four prices to one formula and checks the prices that come back against the published definition,
/// with nothing between the two: no fixture to read a period off, no ATR to scale by, no merge, no cap and
/// no relabelling. <c>PivotLevelMethodTests</c> checks the same formulas through the whole pipeline, which
/// is where the zones and the kinds are pinned.
/// </para>
/// <para>
/// <b>What only this file can show is a line the pipeline never hands back</b>, and the worked period below
/// has none — measured, all five formulas come out of the pipeline with exactly as many zones as they have
/// lines, 7, 7, 8, 5 and 3. So <see cref="Wide"/> is here too: on a period whose range is most of its own
/// low, classic computes <c>-35</c> and <c>32</c> and fibonacci <c>32</c>, and the width cap drops all
/// three before anything is returned. <b>The first version of this paragraph claimed the worked period had
/// such lines and named two that are in fact reported</b>; it was a claim about a run nobody had made, which
/// is the kind of prose this repository treats as a build break.
/// </para>
/// <para>
/// <b>The period is <c>O=100 H=120 L=96 C=111</c>, the same one the pipeline file works</b>, so the two
/// files can be read against each other. It is chosen so every one of the five divides exactly in
/// <see langword="decimal"/>: <c>H + L + C</c> is <c>327</c>, a multiple of three, and the range is
/// <c>24</c>, a multiple of twelve — which is what makes camarilla's <c>1.1 / 12</c> leg land on <c>2.2</c>
/// rather than on a repeating fraction nobody can check by eye.
/// </para>
/// </remarks>
public sealed class PivotFormulaTests
{
    /// <summary>The worked period: opened at 100, ranged 96 to 120, closed at 111.</summary>
    private static PivotPeriod Period => new(Open: 100m, High: 120m, Low: 96m, Close: 111m);

    /// <summary>
    /// A period whose range is most of its own low — the one that sends far legs off the price scale.
    /// </summary>
    /// <remarks>
    /// The period <c>PivotLevelMethodTests.AFormulaThatLeavesThePriceDomain_NeverReachesTheAnswer</c> builds
    /// its bars around, so the two files can be read against each other there as well.
    /// </remarks>
    private static PivotPeriod Wide => new(Open: 100m, High: 200m, Low: 99m, Close: 100m);

    private static IEnumerable<decimal> Prices(PivotFormula formula) =>
        PivotLevels.Lines(formula, Period).Select(line => line.Price);

    private static IEnumerable<KeyLevelKind> Kinds(PivotFormula formula) =>
        PivotLevels.Lines(formula, Period).Select(line => line.Kind);

    [Fact]
    public void ThePeriodsRangeIsItsHighLessItsLow()
    {
        Period.Range.Should().Be(24m);
    }

    [Fact]
    public void Classic_IsThePivotAndThreeLevelsEitherSideOfIt()
    {
        //  P  = (H + L + C) / 3    = (120 + 96 + 111) / 3 = 327 / 3 = 109
        //  S1 = 2P - H             = 218 - 120 =  98        R1 = 2P - L        = 218 - 96  = 122
        //  S2 = P - (H - L)        = 109 -  24 =  85        R2 = P + (H - L)   = 109 + 24  = 133
        //  S3 = L - 2(H - P)       =  96 -  22 =  74        R3 = H + 2(P - L)  = 120 + 26  = 146
        Prices(PivotFormula.Classic).Should().Equal(74m, 85m, 98m, 109m, 122m, 133m, 146m);
    }

    [Fact]
    public void Fibonacci_KeepsTheClassicPivotAndMovesItsLegsOntoTheRange()
    {
        //  P = 109, exactly as classic; only the legs differ, and each is a fraction of the 24-point range.
        //    0.382 * 24 =  9.168     0.618 * 24 = 14.832     1.000 * 24 = 24
        Prices(PivotFormula.Fibonacci).Should().Equal(
            85m,       // S3 = 109 - 24
            94.168m,   // S2 = 109 - 14.832
            99.832m,   // S1 = 109 -  9.168
            109m,      // P
            118.168m,  // R1 = 109 +  9.168
            123.832m,  // R2 = 109 + 14.832
            133m);     // R3 = 109 + 24
    }

    [Fact]
    public void Camarilla_IsEightLegsAroundThePriorClose_AndCarriesNoCentralPivot()
    {
        //  Measured from C = 111 rather than from a pivot, at range * 1.1 / n:  24 * 1.1 = 26.4
        //    /12 = 2.2     /6 = 4.4     /4 = 6.6     /2 = 13.2
        //
        //  Eight lines, deliberately. Adding (H + L + C) / 3 to this set would report classic's line under
        //  camarilla's name, and the two are not the same claim about the same period.
        Prices(PivotFormula.Camarilla).Should().Equal(
            97.8m,   // S4 = 111 - 13.2
            104.4m,  // S3 = 111 -  6.6
            106.6m,  // S2 = 111 -  4.4
            108.8m,  // S1 = 111 -  2.2
            113.2m,  // R1 = 111 +  2.2
            115.4m,  // R2 = 111 +  4.4
            117.6m,  // R3 = 111 +  6.6
            124.2m); // R4 = 111 + 13.2
    }

    [Fact]
    public void Woodie_WeighsTheCloseTwice_SoItsPivotSitsHalfAPointAboveClassics()
    {
        //  P = (H + L + 2C) / 4 = (120 + 96 + 222) / 4 = 438 / 4 = 109.5, against classic's 109.
        //  S1 = 2P - H = 219 - 120 =  99      R1 = 2P - L      = 219 - 96 = 123
        //  S2 = P - (H - L) = 85.5            R2 = P + (H - L) = 133.5
        Prices(PivotFormula.Woodie).Should().Equal(85.5m, 99m, 109.5m, 123m, 133.5m);
    }

    // ── DeMark's three branches, from three periods differing only in the open ────────────────────────

    [Theory]
    [InlineData(100, 447, 103.5, 111.75, 127.5)] // C (111) above the open: X = 2H + L + C = 240 + 96 + 111
    [InlineData(120, 423, 91.5, 105.75, 115.5)]  // C below the open:       X = H + 2L + C = 120 + 192 + 111
    [InlineData(111, 438, 99, 109.5, 123)]       // C exactly at the open:  X = H + L + 2C = 120 +  96 + 222
    public void DeMark_ChoosesItsBranchOnTheCloseAgainstTheOpen(
        int open, int expectedX, double s1, double pivot, double r1)
    {
        //  All three share H = 120, L = 96, C = 111 and differ only in where the period opened, so the only
        //  thing moving is the branch. S1 = X/2 - H, P = X/4, R1 = X/2 - L.
        PivotPeriod period = Period with { Open = open };

        PivotLevels.Lines(PivotFormula.DeMark, period).Select(line => line.Price)
            .Should().Equal((decimal)s1, (decimal)pivot, (decimal)r1);

        // The X the three prices are all derived from, recovered from the pivot so the branch is named as a
        // number rather than only as three consequences of one.
        (PivotLevels.Lines(PivotFormula.DeMark, period)[1].Price * 4m).Should().Be((decimal)expectedX);
    }

    // ── The seed each formula puts on its own legs ────────────────────────────────────────────────────

    [Fact]
    public void EveryFormulaNamesItsOwnLegs_AndLeavesOnlyThePivotItselfUnseeded()
    {
        // A published set labels its legs R or S, and that naming is what `ApplyClose` keeps when the current
        // price sits inside the zone -- the one place a formation's own reading is the honest one (`R-3.3`).
        // The pivot itself is neither by name, so it comes back Unknown and `PivotLevels.Compute` seeds it
        // from the current close instead. Camarilla has no pivot line at all, so it has no Unknown.
        Kinds(PivotFormula.Classic).Should().Equal(
            KeyLevelKind.Support, KeyLevelKind.Support, KeyLevelKind.Support,
            KeyLevelKind.Unknown,
            KeyLevelKind.Resistance, KeyLevelKind.Resistance, KeyLevelKind.Resistance);

        Kinds(PivotFormula.Camarilla).Should().NotContain(KeyLevelKind.Unknown);

        Kinds(PivotFormula.DeMark).Should().Equal(
            KeyLevelKind.Support, KeyLevelKind.Unknown, KeyLevelKind.Resistance);
    }

    // ── The lines the pipeline never hands back ───────────────────────────────────────────────────────

    [Fact]
    public void OnAWidePeriod_TheFarLegsRunOffThePriceScale_AndOnlyThisFileEverSeesThem()
    {
        //  P = (200 + 99 + 100) / 3 = 399 / 3 = 133, range = 101.
        //    S3 = L - 2(H - P) =  99 - 134 = -35   a price no instrument can trade at
        //    S2 = P - (H - L)  = 133 - 101 =  32   a real price, but a 1-point zone is 3.1% of it
        //
        // Neither reaches an answer: the width cap needs `width * 100 <= MaxZoneWidthPercent * midpoint`,
        // which at the shipped 2.5% wants a midpoint of at least 40 and can never hold at or below zero,
        // because a zone's width is always positive. So the arithmetic below is visible HERE and nowhere
        // else, which is what having a formula-only file buys.
        // `PivotLevelMethodTests.AFormulaThatLeavesThePriceDomain_NeverReachesTheAnswer` is the other half:
        // the same period, through the pipeline, coming back as five zones rather than seven.
        PivotLevels.Lines(PivotFormula.Classic, Wide).Select(line => line.Price)
            .Should().Equal(-35m, 32m, 66m, 133m, 167m, 234m, 268m);

        // Fibonacci's own S3 lands on the same 32, from a different route: P - 1.000 * range.
        PivotLevels.Lines(PivotFormula.Fibonacci, Wide)[0].Price.Should().Be(32m);
    }

    [Theory]
    [InlineData(PivotFormula.Classic, 7)]
    [InlineData(PivotFormula.Fibonacci, 7)]
    [InlineData(PivotFormula.Camarilla, 8)]
    [InlineData(PivotFormula.Woodie, 5)]
    [InlineData(PivotFormula.DeMark, 3)]
    public void TheWorkedPeriodHasNoSuchLine_WhichIsWhyTheWideOneIsHere(PivotFormula formula, int lines)
    {
        // The claim this file's remarks used to make about the worked period, turned into a run.
        //
        // At the pipeline file's ATR of 2 and the shipped 0.5 zone width, every zone is exactly ONE point
        // wide, and the width cap keeps a zone when `1 * 100 <= 2.5 * midpoint` -- so it bites below a
        // midpoint of 40 and nowhere else. Every line the five compute from O=100 H=120 L=96 C=111 is at 74
        // or above, so all of them survive: line counts and zone counts are equal formula for formula, and
        // nothing about a dropped line is observable from this period at all.
        IReadOnlyList<PivotLine> computed = PivotLevels.Lines(formula, Period);

        computed.Should().HaveCount(lines);
        computed.Select(line => line.Price).Min().Should().BeGreaterThanOrEqualTo(
            40m, "below 40 the shipped width cap drops a one-point zone, and this period reaches no such line");
    }

    // ── Every formula answers, and an unset one does not ──────────────────────────────────────────────

    [Fact]
    public void EveryServableFormula_ReturnsLines()
    {
        // Read off the enum rather than listed, so a sixth formula is swept by being written. `NotThrow`
        // would pass for `return [];` (PR #252, finding 2), so this asserts a count.
        foreach (PivotFormula formula in Enum.GetValues<PivotFormula>().Where(f => f != PivotFormula.Unknown))
        {
            PivotLevels.Lines(formula, Period).Should().NotBeEmpty(formula + " computed no lines at all");
        }
    }

    [Fact]
    public void AnUnsetFormula_IsRefused()
    {
        Action unset = () => PivotLevels.Lines(PivotFormula.Unknown, Period);

        unset.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Unknown*");
    }

    [Theory]
    [InlineData(0)]  // Unknown, what an unset value binds to
    [InlineData(6)]  // one past the vocabulary
    [InlineData(99)] // nowhere near it
    public void AFormulaOutsideTheVocabulary_IsRefusedByBothEntryPoints_WithTheSameMessage(int raw)
    {
        // The lesson `KeyLevels` learned about `PivotSource`: a cast integer outside the enum is not
        // `Unknown`, and a switch whose default arm computed something would answer with a level set nobody
        // named. Both entry points refuse it, and they say the same thing -- a value described one way by
        // `NameOf` and another by `Lines` is a value described by whichever path happened to catch it.
        Action named = () => PivotLevels.NameOf((PivotFormula)raw);
        Action lined = () => PivotLevels.Lines((PivotFormula)raw, Period);

        named.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Classic, Fibonacci, Camarilla*");
        lined.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Classic, Fibonacci, Camarilla*");
    }
}
