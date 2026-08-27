using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The four stages of <see cref="KeyLevels"/> — <c>FindPivots</c>, <c>ZoneFor</c>, <c>MergeOverlapping</c>,
/// <c>ApplyClose</c> — against hand-checked numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every expected value here was worked out by hand from the definition and the derivation is written
/// beside it, never captured from a run.</b> A fixture built from today's output pins a bug exactly as firmly
/// as it pins the behaviour, and would then defend that bug against every later change. The series are small
/// enough that a reader can redo the arithmetic, and every number is exact in decimal — the denominators are
/// 2 and 4, so nothing here needs an approximate comparison that could hide real drift.
/// </para>
/// <para>
/// <b>Which case pins which stage.</b> Each stage has at least one case that goes red if that stage is
/// removed entirely, so the suite says <i>which</i> rule broke rather than going red as a block:
/// </para>
/// <list type="table">
/// <item>
/// <term><c>FindPivots</c></term>
/// <description>
/// <see cref="FindPivots_FindsOnlyBarsThatDominateBothSidesOfTheLookback"/> — a pass-through that returned
/// every bar gives seven pivots, not three; one that returned nothing gives zero. The window's own width is
/// pinned separately by <see cref="FindPivots_WeighsBothOutermostBarsOfTheLookbackWindow"/>, because a
/// symmetric fixture survives an off-by-one at either end of it unchanged.
/// </description>
/// </item>
/// <item>
/// <term><c>ZoneFor</c></term>
/// <description>
/// <see cref="ZoneFor_SizesTheZoneToTheAtrMultiple"/> and
/// <see cref="Detect_SizesEachZoneWithTheAtrAtItsOwnPivotBar"/> — an unscaled zone collapses to the pivot
/// price, which is a different bound and a different merge.
/// </description>
/// </item>
/// <item>
/// <term><c>MergeOverlapping</c></term>
/// <description>
/// <see cref="MergeOverlapping_KeepsTheEarliestFormationTheStrongestSignificanceAndTheSumOfTouches"/> and
/// <see cref="Detect_ProducesTheHandDerivedZonesForTheWorkedFixture"/> — removed, the pipeline reports three
/// zones instead of two and the surviving touch count is 1 rather than 2.
/// </description>
/// </item>
/// <item>
/// <term><c>ApplyClose</c></term>
/// <description>
/// <see cref="ApplyClose_RelabelsAZoneEntirelyBelowTheCloseAsSupport"/> and
/// <see cref="Detect_RelabelsAnOldResistanceAsSupportOnceTheCloseIsAboveIt"/> — removed, a level price has
/// already broken above is still reported as a ceiling underneath the market.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>And which case pins the shared preconditions.</b> <c>FindPivots</c> opens with both of
/// <c>IndicatorGuard</c>'s series checks, and the two are pinned from opposite directions. The roll check is
/// reached from <c>ContractRollTests.KeyLevels_AreNotDetectedAcrossASplice</c>; the ordering check was
/// reached by nothing at all, so deleting its call left the whole suite green (gh#251). That is what
/// <see cref="FindPivots_Refuses_WhenTwoBarsAreTransposed"/> and
/// <see cref="Detect_Refuses_WhenTwoBarsAreTransposed"/> close, and the counterweight is every case above
/// them: the same fixture <i>in order</i> still computes, so the guard is pinned as refusing the disorder
/// and nothing else.
/// </para>
/// </remarks>
public sealed class KeyLevelsTests
{
    /// <summary>The open time of bar <paramref name="index"/> — five-minute buckets from a fixed origin.</summary>
    private static DateTimeOffset At(int index) =>
        new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero).AddMinutes(5 * index);

    /// <summary>
    /// A bar written as just its high and its low, opening at the low and closing at the high.
    /// </summary>
    /// <remarks>
    /// The fixtures built from this one are read with <see cref="PivotSource.HighLow"/>, which looks at
    /// nothing else; the pipeline additionally reads the <b>last</b> bar's close, which is that bar's high
    /// here. <see cref="PivotSource.Body"/> and <see cref="PivotSource.HeikinAshiBody"/> need open and close
    /// set independently, so they get their own fixture below.
    /// </remarks>
    private static Bar HighLowBar(int index, decimal high, decimal low) =>
        new(At(index), Open: low, High: high, Low: low, Close: high, Volume: 100);

    /// <summary>A bar with every price set explicitly.</summary>
    private static Bar Ohlc(int index, decimal open, decimal high, decimal low, decimal close) =>
        new(At(index), open, high, low, close, Volume: 100);

    /// <summary>The options the worked fixtures are derived under.</summary>
    /// <remarks>
    /// <para>
    /// A lookback of 2 on <b>both</b> sides keeps the dominance window five bars wide, which a reader can
    /// check at a glance. The zone multiple and significance floor are the production defaults.
    /// </para>
    /// <para>
    /// <b>Every field is stated, including the two caps, and the shipped defaults are deliberately not
    /// inherited here.</b> These fixtures run an ATR of 4 against a price near 110 — 3.6% of price, where a
    /// real five-minute future is nearer 0.1% — so a width cap calibrated for an instrument would delete
    /// zones this file exists to pin. The caps are pinned on their own fixtures instead, in
    /// <see cref="KeyLevelBjorgumBehaviourTests"/>, where the numbers are chosen to make them fire.
    /// </para>
    /// </remarks>
    private static KeyLevelOptions HighLowOptions => new(
        Lookback: 2,
        Source: PivotSource.HighLow,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m,
        RightLookback: 2,
        MaxZoneWidthPercent: 100m,
        MaxLevels: 1_000);

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  FIXTURE A — the worked series. Eleven bars, read High/Low, lookback 2.
    //
    //    i:      0     1     2     3     4     5     6     7     8     9    10
    //    high: 104   106   110   106   104   102   104   106   112   106   104
    //    low:  100   102   106   102   100    98   100   102   108   102   100
    //
    //  A pivot needs `i` in [lookback, count - lookback), i.e. 2..8, and must strictly dominate the two
    //  bars either side. Working every eligible index by hand:
    //
    //    i=2  high 110 vs {104,106,106,104} -> dominates. Prominence 110 - 106 =  4. Resistance.
    //    i=3  high 106 ties bar 1's 106 -> no.   low 102 ties bar 1's 102 -> no.
    //    i=4  high 104 under bar 2's 110 -> no.  low 100 over bar 5's 98  -> no.
    //    i=5  low   98 vs {102,100,100,102} -> dominates. Prominence 100 -  98 =  2. Support.
    //    i=6  high 104 ties bar 4's 104 -> no.   low 100 ties bar 4's 100 -> no.
    //    i=7  high 106 under bar 8's 112 -> no.  low 102 over bar 5's 98  -> no.
    //    i=8  high 112 vs {104,106,106,104} -> dominates. Prominence 112 - 106 =  6. Resistance.
    //
    //  So: three pivots — (2, 110, Resistance, 4), (5, 98, Support, 2), (8, 112, Resistance, 6).
    //  The last close is bar 10's, which is 104.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> FixtureA =>
    [
        HighLowBar(0, high: 104, low: 100),
        HighLowBar(1, high: 106, low: 102),
        HighLowBar(2, high: 110, low: 106),
        HighLowBar(3, high: 106, low: 102),
        HighLowBar(4, high: 104, low: 100),
        HighLowBar(5, high: 102, low: 98),
        HighLowBar(6, high: 104, low: 100),
        HighLowBar(7, high: 106, low: 102),
        HighLowBar(8, high: 112, low: 108),
        HighLowBar(9, high: 106, low: 102),
        HighLowBar(10, high: 104, low: 100),
    ];

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  FIXTURE B — the breakout. Bars 0..7 are fixture A's; the last three run away upward.
    //
    //    i:      0     1     2     3     4     5     6     7     8     9    10
    //    high: 104   106   110   106   104   102   104   106   108   120   130
    //    low:  100   102   106   102   100    98   100   102   104   110   120
    //
    //    i=2  high 110 vs {104,106,106,104} -> dominates. Prominence 4. Resistance.
    //    i=5  low   98 vs {102,100,100,102} -> dominates. Prominence 2. Support.
    //    i=6  high 104 ties bar 4's 104 -> no.   low 100 ties bar 4's 100 -> no.
    //    i=7  high 106 under bar 9's 120 -> no.  low 102 over bar 5's 98  -> no.
    //    i=8  high 108 under bar 9's 120 -> no.  low 104 over bar 4's 100 -> no.
    //
    //  Two pivots, both formed early; the last close is bar 10's high, 130, which is above both.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> FixtureB =>
    [
        HighLowBar(0, high: 104, low: 100),
        HighLowBar(1, high: 106, low: 102),
        HighLowBar(2, high: 110, low: 106),
        HighLowBar(3, high: 106, low: 102),
        HighLowBar(4, high: 104, low: 100),
        HighLowBar(5, high: 102, low: 98),
        HighLowBar(6, high: 104, low: 100),
        HighLowBar(7, high: 106, low: 102),
        HighLowBar(8, high: 108, low: 104),
        HighLowBar(9, high: 120, low: 110),
        HighLowBar(10, high: 130, low: 120),
    ];

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  FIXTURE S — one series, three sources, three different answers. Five bars, lookback 2, so only
    //  index 2 is eligible and there is exactly one place for a pivot to be.
    //
    //    i:    O      H      L      C
    //    0:  100    102     98    101
    //    1:  101    103     99    102
    //    2:  103    120    102    106      <- a long upper wick above a small body
    //    3:  104    105    101    103
    //    4:  103    104    100    102
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> FixtureS =>
    [
        Ohlc(0, open: 100, high: 102, low: 98, close: 101),
        Ohlc(1, open: 101, high: 103, low: 99, close: 102),
        Ohlc(2, open: 103, high: 120, low: 102, close: 106),
        Ohlc(3, open: 104, high: 105, low: 101, close: 103),
        Ohlc(4, open: 103, high: 104, low: 100, close: 102),
    ];

    /// <summary>Fixture A with the bars at <paramref name="i"/> and <paramref name="j"/> exchanged.</summary>
    /// <param name="i">One position in the series.</param>
    /// <param name="j">The other.</param>
    /// <returns>The same eleven bars, two of them out of time order.</returns>
    /// <remarks>
    /// Not one price is touched, and that is the point: this series is exactly as computable as fixture A
    /// and differs from it only in the order the bars arrive. Disorder that changed the numbers would be
    /// caught by the arithmetic; disorder that does not is what needs a guard.
    /// </remarks>
    private static IReadOnlyList<Bar> FixtureATransposed(int i, int j)
    {
        List<Bar> bars = [.. FixtureA];
        (bars[i], bars[j]) = (bars[j], bars[i]);
        return bars;
    }

    // ═══ FindPivots ═══════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FindPivots_FindsOnlyBarsThatDominateBothSidesOfTheLookback()
    {
        // The derivation for all seven eligible indices is beside fixture A. This is the case that goes red
        // if the dominance test is removed: without it every one of indices 2..8 is a "pivot", seven of them.
        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(FixtureA, HighLowOptions);

        pivots.Should().Equal(
            new SwingPivot(2, At(2), 110m, KeyLevelKind.Resistance, 4m),
            new SwingPivot(5, At(5), 98m, KeyLevelKind.Support, 2m),
            new SwingPivot(8, At(8), 112m, KeyLevelKind.Resistance, 6m));
    }

    [Fact]
    public void FindPivots_MeasuresProminenceAgainstTheStrongestRivalInTheWindow()
    {
        // Prominence is how far the pivot stands clear of the next-best price of its own kind inside the
        // window — not the window's opposite extreme. Bar 2's high is 110 and the best rival high in
        // bars 0..4 is 106, so 4. Measured against the window's lowest low (100) it would read 10; against
        // the whole series' second-highest high (112, bar 8, outside the window) it would read -2.
        IReadOnlyList<SwingPivot> pivots = KeyLevels.FindPivots(FixtureA, HighLowOptions);

        pivots[0].Prominence.Should().Be(4m);
        pivots[1].Prominence.Should().Be(2m); // low 98, best rival low in bars 3..7 is 100.
        pivots[2].Prominence.Should().Be(6m); // high 112, best rival high in bars 6..10 is 106.
    }

    [Fact]
    public void FindPivots_ReturnsNothing_WhenTheSeriesIsShorterThanTwoLookbacksPlusOne()
    {
        // Lookback 2 needs 2 + 1 + 2 = 5 bars before any index can be surrounded. Four cannot.
        IReadOnlyList<Bar> tooShort = [.. FixtureA.Take(4)];

        KeyLevels.FindPivots(tooShort, HighLowOptions).Should().BeEmpty();
    }

    [Fact]
    public void FindPivots_FindsAPivotOnTheVeryFirstEligibleSeriesLength()
    {
        // Exactly 2 * lookback + 1 bars: index 2 is the only eligible one, and it dominates.
        // High 110 against {102,103,105,104}; the best rival is 105, so the prominence is 5.
        IReadOnlyList<Bar> exactlyEnough =
        [
            HighLowBar(0, high: 102, low: 98),
            HighLowBar(1, high: 103, low: 99),
            HighLowBar(2, high: 110, low: 106),
            HighLowBar(3, high: 105, low: 101),
            HighLowBar(4, high: 104, low: 100),
        ];

        KeyLevels.FindPivots(exactlyEnough, HighLowOptions).Should().Equal(
            new SwingPivot(2, At(2), 110m, KeyLevelKind.Resistance, 5m));
    }

    [Fact]
    public void FindPivots_FindsThePivotSittingOnTheLastEligibleIndex()
    {
        // Seven bars, lookback 2: eligible indices are 2, 3 and 4. The peak is at 4 — the last one — and
        // it dominates {102,103,102,101}, best rival 103, so the prominence is 7.
        //   high: 100  101  102  103  110  102  101
        //   low:   96   97   98   99  106   98   97
        IReadOnlyList<Bar> peakAtTheEdge =
        [
            HighLowBar(0, high: 100, low: 96),
            HighLowBar(1, high: 101, low: 97),
            HighLowBar(2, high: 102, low: 98),
            HighLowBar(3, high: 103, low: 99),
            HighLowBar(4, high: 110, low: 106),
            HighLowBar(5, high: 102, low: 98),
            HighLowBar(6, high: 101, low: 97),
        ];

        KeyLevels.FindPivots(peakAtTheEdge, HighLowOptions).Should().Equal(
            new SwingPivot(4, At(4), 110m, KeyLevelKind.Resistance, 7m));
    }

    [Fact]
    public void FindPivots_NeverFindsAPivotInsideTheTrailingLookback()
    {
        // The same shape as the case above with the peak moved one bar later, to index 5 — inside the
        // trailing lookback. It is the highest bar in the series by six points and it still yields nothing,
        // because a "pivot" confirmed only by the bars before it repaints as soon as the next one arrives.
        //   high: 100  101  102  103  104  110  101
        //   low:   96   97   98   99  100  106   97
        // Eligible indices are 2, 3, 4: 102 loses to 103, 103 loses to 110, 104 loses to 110; and every low
        // loses to a lower neighbour.
        IReadOnlyList<Bar> peakPastTheEdge =
        [
            HighLowBar(0, high: 100, low: 96),
            HighLowBar(1, high: 101, low: 97),
            HighLowBar(2, high: 102, low: 98),
            HighLowBar(3, high: 103, low: 99),
            HighLowBar(4, high: 104, low: 100),
            HighLowBar(5, high: 110, low: 106),
            HighLowBar(6, high: 101, low: 97),
        ];

        KeyLevels.FindPivots(peakPastTheEdge, HighLowOptions).Should().BeEmpty();
        peakPastTheEdge.Max(b => b.High).Should().Be(110m, "the bar that yields nothing is the series' highest");
    }

    [Fact]
    public void FindPivots_WeighsBothOutermostBarsOfTheLookbackWindow()
    {
        // The window is [i - lookback, i + lookback] inclusive at BOTH ends, and a fixture that does not
        // put a decisive rival on each outermost bar cannot tell a correct window from one that is a bar
        // short. Two series, each with exactly one rival and it sitting on an edge.
        //
        // Right edge. Bar 2's high of 105 is beaten only by bar 4's 106 — exactly lookback bars later:
        //   high: 100  101  105  102  106  101  100
        //   low:   96   97   99   98   95   97   96
        // Eligible indices are 2, 3, 4. Bar 2 loses its high to bar 4 and its low to bar 4's 95; bar 3 loses
        // both to bar 2 and bar 4; bar 4's 106 beats {105, 102, 101, 100}, best rival 105, so prominence 1.
        // One pivot. A window that stopped at i + lookback - 1 would not see bar 4 from bar 2 and would
        // report a second, spurious pivot at 105.
        IReadOnlyList<Bar> rivalOnTheRightEdge =
        [
            HighLowBar(0, high: 100, low: 96),
            HighLowBar(1, high: 101, low: 97),
            HighLowBar(2, high: 105, low: 99),
            HighLowBar(3, high: 102, low: 98),
            HighLowBar(4, high: 106, low: 95),
            HighLowBar(5, high: 101, low: 97),
            HighLowBar(6, high: 100, low: 96),
        ];

        // Left edge, mirrored. Bar 2's high of 105 is beaten only by bar 0's 106 — exactly lookback bars
        // earlier — and nothing else in the series dominates anything:
        //   high: 106  101  105  102  100   99   98
        //   low:   95   97   99   98   96   94   93
        // Bar 3's 102 loses to bar 2's 105 and bar 4's 100 loses to it too; every low is beaten by bar 5's
        // 94 or bar 0's 95. No pivots at all. A window that started at i - lookback + 1 would not see bar 0
        // from bar 2 and would report one.
        IReadOnlyList<Bar> rivalOnTheLeftEdge =
        [
            HighLowBar(0, high: 106, low: 95),
            HighLowBar(1, high: 101, low: 97),
            HighLowBar(2, high: 105, low: 99),
            HighLowBar(3, high: 102, low: 98),
            HighLowBar(4, high: 100, low: 96),
            HighLowBar(5, high: 99, low: 94),
            HighLowBar(6, high: 98, low: 93),
        ];

        KeyLevels.FindPivots(rivalOnTheRightEdge, HighLowOptions).Should().Equal(
            new SwingPivot(4, At(4), 106m, KeyLevelKind.Resistance, 1m));

        KeyLevels.FindPivots(rivalOnTheLeftEdge, HighLowOptions).Should().BeEmpty();
    }

    [Fact]
    public void FindPivots_RejectsABarThatOnlyTiesItsNeighbour()
    {
        // Dominance is strict. These two series differ in one number — bar 1's high — and only the second
        // has a pivot. Tied at 105 the candidate is refused; with the rival one point lower it stands clear
        // by exactly that point.
        IReadOnlyList<Bar> tied =
        [
            HighLowBar(0, high: 100, low: 95),
            HighLowBar(1, high: 105, low: 96),
            HighLowBar(2, high: 105, low: 96),
            HighLowBar(3, high: 101, low: 94),
            HighLowBar(4, high: 100, low: 93),
        ];

        IReadOnlyList<Bar> broken =
        [
            HighLowBar(0, high: 100, low: 95),
            HighLowBar(1, high: 104, low: 96),
            HighLowBar(2, high: 105, low: 96),
            HighLowBar(3, high: 101, low: 94),
            HighLowBar(4, high: 100, low: 93),
        ];

        KeyLevels.FindPivots(tied, HighLowOptions).Should().BeEmpty();
        KeyLevels.FindPivots(broken, HighLowOptions).Should().Equal(
            new SwingPivot(2, At(2), 105m, KeyLevelKind.Resistance, 1m));
    }

    [Fact]
    public void FindPivots_ReportsAnEngulfingBarAsResistanceOnly()
    {
        // Bar 2 engulfs its whole window: its high dominates AND its low dominates. Both readings are
        // available — a resistance at 110 standing clear of 101 by 9, or a support at 90 standing clear of
        // 97 by 7 — and one bar yields one pivot, the high winning.
        //
        // This precedence is not stated anywhere in the type's documentation; the case is here so that
        // changing it (to emit both, or to prefer the larger prominence) is a deliberate act with a red test
        // in front of it rather than a silent change of output.
        IReadOnlyList<Bar> engulfing =
        [
            HighLowBar(0, high: 100, low: 98),
            HighLowBar(1, high: 101, low: 97),
            HighLowBar(2, high: 110, low: 90),
            HighLowBar(3, high: 101, low: 97),
            HighLowBar(4, high: 100, low: 98),
        ];

        KeyLevels.FindPivots(engulfing, HighLowOptions).Should().Equal(
            new SwingPivot(2, At(2), 110m, KeyLevelKind.Resistance, 9m));
    }

    [Fact]
    public void FindPivots_ReadsTheRawWicks_WhenTheSourceIsHighLow()
    {
        // Fixture S, HighLow: highs 102, 103, 120, 105, 104 and lows 98, 99, 102, 101, 100.
        // Index 2's high of 120 dominates; the best rival is bar 3's 105, so the prominence is 15.
        IReadOnlyList<SwingPivot> pivots =
            KeyLevels.FindPivots(FixtureS, HighLowOptions with { Source = PivotSource.HighLow });

        pivots.Should().Equal(new SwingPivot(2, At(2), 120m, KeyLevelKind.Resistance, 15m));
    }

    [Fact]
    public void FindPivots_IgnoresTheWicks_WhenTheSourceIsBody()
    {
        // The same bars read as max/min of open and close: highs 101, 102, 106, 104, 103 and lows
        // 100, 101, 103, 103, 102. Index 2's body top of 106 dominates; the best rival is bar 3's 104,
        // so the prominence is 2. Same series, a different level and a different score — which is what
        // proves the source was actually consulted.
        IReadOnlyList<SwingPivot> pivots =
            KeyLevels.FindPivots(FixtureS, HighLowOptions with { Source = PivotSource.Body });

        pivots.Should().Equal(new SwingPivot(2, At(2), 106m, KeyLevelKind.Resistance, 2m));
    }

    [Fact]
    public void FindPivots_SmoothsThroughTheHeikinAshiBody_WhenThatIsTheSource()
    {
        // haClose[i] = (O + H + L + C) / 4; haOpen[0] = (O[0] + C[0]) / 2; haOpen[i] = (haOpen[i-1] + haClose[i-1]) / 2.
        //
        //   i  haClose                      haOpen                                 top          bottom
        //   0  (100+102+98+101)/4 = 100.25  (100+101)/2         = 100.5            100.5        100.25
        //   1  (101+103+99+102)/4 = 101.25  (100.5+100.25)/2    = 100.375          101.25       100.375
        //   2  (103+120+102+106)/4 = 107.75 (100.375+101.25)/2  = 100.8125         107.75       100.8125
        //   3  (104+105+101+103)/4 = 103.25 (100.8125+107.75)/2 = 104.28125        104.28125    103.25
        //   4  (103+104+100+102)/4 = 102.25 (104.28125+103.25)/2 = 103.765625      103.765625   102.25
        //
        // Index 2's top of 107.75 dominates {100.5, 101.25, 104.28125, 103.765625}; the best rival is
        // 104.28125, so the prominence is 107.75 - 104.28125 = 3.46875. Every value terminates in decimal,
        // so these are exact. Note the smoothing carrying forward: bar 3's haOpen is pulled up to 104.28125
        // by bar 2's long wick even though bar 3 itself never traded there.
        IReadOnlyList<SwingPivot> pivots =
            KeyLevels.FindPivots(FixtureS, HighLowOptions with { Source = PivotSource.HeikinAshiBody });

        pivots.Should().Equal(new SwingPivot(2, At(2), 107.75m, KeyLevelKind.Resistance, 3.46875m));
    }

    [Fact]
    public void FindPivots_Refuses_WhenTheSourceIsUnset()
    {
        // Unknown must be refused, never quietly resolved to the default: a level measured from a source
        // nobody chose is a number that looks ordinary and is acted on as one.
        Action find = () => KeyLevels.FindPivots(FixtureA, HighLowOptions with { Source = PivotSource.Unknown });

        find.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*source*");
    }

    [Fact]
    public void FindPivots_Refuses_WhenTheLookbackIsBelowOne()
    {
        Action find = () => KeyLevels.FindPivots(FixtureA, HighLowOptions with { Lookback = 0 });

        find.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*lookback*");
    }

    [Fact]
    public void FindPivots_Refuses_WhenTwoBarsAreTransposed()
    {
        // Fixture A with bars 4 and 5 exchanged, so the bar at index 5 opens BEFORE the one at index 4.
        // Unguarded, PivotPrices builds the highs and lows in whatever order it was handed and the
        // dominance scan compares each bar against neighbours that are not its neighbours.
        //
        //    i:      0     1     2     3     4     5     6     7     8     9    10
        //    high: 104   106   110   106   102   104   104   106   112   106   104
        //    low:  100   102   106   102    98   100   100   102   108   102   100
        //    time:  t0    t1    t2    t3    t5    t4    t6    t7    t8    t9   t10
        //
        //    i=2  high 110 vs {104,106,106,102} -> dominates. Prominence 4. Resistance.
        //    i=3  high 106 ties bar 1's 106 -> no.   low 102 ties bar 1's 102 -> no.
        //    i=4  high 102 under bar 2's 110 -> no.  low  98 vs {106,102,100,100} -> dominates.
        //                                            Prominence 100 - 98 = 2. Support.
        //    i=5  high 104 ties bar 6's 104 -> no.   low 100 ties bar 6's 100 -> no.
        //    i=6  high 104 ties bar 5's 104 -> no.   low 100 ties bar 5's 100 -> no.
        //    i=7  high 106 under bar 8's 112 -> no.  low 102 over bar 5's 100 -> no.
        //    i=8  high 112 vs {104,106,106,104} -> dominates. Prominence 6. Resistance.
        //
        // So the answer that comes back is not a mangled one a caller could spot -- it is fixture A's three
        // pivots at fixture A's prices, prominences, kinds and OpenTimes, with ONE field moved: the support
        // is reported at BarIndex 4 rather than 5. Detect reads `atr[pivot.BarIndex]`, so that pivot is then
        // sized and scored from the ATR of a bar it did not form on. A flat ATR hides it completely; a real
        // one gives a different half-band and a different significance, and neither says so.
        Action find = () => KeyLevels.FindPivots(FixtureATransposed(4, 5), HighLowOptions);

        find.Should().Throw<ArgumentException>()
            .WithMessage("*strictly ascending*")
            .WithParameterName("bars");
    }

    // ═══ ZoneFor ══════════════════════════════════════════════════════════════════════════════════════

    private static SwingPivot Pivot(decimal price, decimal prominence, KeyLevelKind kind = KeyLevelKind.Resistance) =>
        new(BarIndex: 4, OpenTime: At(4), Price: price, Kind: kind, Prominence: prominence);

    [Fact]
    public void ZoneFor_SizesTheZoneToTheAtrMultiple()
    {
        // Full width is atr * ZoneAtrMultiple, so the half-band is atr * multiple / 2 either side.
        //   atr 2, multiple 0.5 -> half-band 0.5 -> [109.5, 110.5], width 1
        //   atr 4, multiple 0.5 -> half-band 1.0 -> [109.0, 111.0], width 2
        // Doubling the ATR doubles the width. This is the case that goes red if the scaling is removed:
        // an unscaled zone collapses to [110, 110].
        SwingPivot pivot = Pivot(price: 110m, prominence: 4m);

        KeyLevelZone narrow = KeyLevels.ZoneFor(pivot, atr: 2m, HighLowOptions)!;
        KeyLevelZone wide = KeyLevels.ZoneFor(pivot, atr: 4m, HighLowOptions)!;

        narrow.Bottom.Should().Be(109.5m);
        narrow.Top.Should().Be(110.5m);
        wide.Bottom.Should().Be(109m);
        wide.Top.Should().Be(111m);
        (wide.Top - wide.Bottom).Should().Be(2m * (narrow.Top - narrow.Bottom));
    }

    [Fact]
    public void ZoneFor_ScalesTheWidthWithTheConfiguredMultipleToo()
    {
        // atr 3, multiple 2 -> half-band 3 -> [97, 103], full width 6 = 3 * 2.
        KeyLevelZone zone = KeyLevels.ZoneFor(
            Pivot(price: 100m, prominence: 30m),
            atr: 3m,
            HighLowOptions with { ZoneAtrMultiple = 2m })!;

        zone.Bottom.Should().Be(97m);
        zone.Top.Should().Be(103m);
    }

    [Fact]
    public void ZoneFor_CentresTheZoneOnThePivotAndCarriesItsIdentityThrough()
    {
        // A fresh zone is one touch, dated from the pivot bar, wearing the pivot's own kind.
        SwingPivot pivot = Pivot(price: 110m, prominence: 4m, kind: KeyLevelKind.Support);

        KeyLevelZone zone = KeyLevels.ZoneFor(pivot, atr: 4m, HighLowOptions)!;

        zone.Midpoint.Should().Be(110m);
        zone.TouchCount.Should().Be(1);
        zone.FormedAtBucket.Should().Be(At(4));
        zone.Kind.Should().Be(KeyLevelKind.Support);
        zone.Contains(109m).Should().BeTrue();
        zone.Contains(111m).Should().BeTrue("the edges are inside the zone");
        zone.Contains(111.01m).Should().BeFalse();
    }

    [Fact]
    public void ZoneFor_ScoresSignificanceAsProminenceInAtrMultiples()
    {
        // Prominence 6 against an ATR of 4 is 1.5 ATR of clearance; against an ATR of 8 the same six points
        // are only 0.75 — half as meaningful in an instrument that moves twice as much.
        SwingPivot pivot = Pivot(price: 112m, prominence: 6m);

        KeyLevels.ZoneFor(pivot, atr: 4m, HighLowOptions)!.Significance.Should().Be(1.5m);
        KeyLevels.ZoneFor(pivot, atr: 8m, HighLowOptions)!.Significance.Should().Be(0.75m);
    }

    [Fact]
    public void ZoneFor_KeepsAPivotSittingExactlyOnTheSignificanceFloor()
    {
        // Prominence 5 over ATR 10 is 0.5, which is the floor exactly. The filter is "below the floor",
        // so equality survives — and the pivot one tick less prominent does not.
        KeyLevels.ZoneFor(Pivot(100m, prominence: 5m), atr: 10m, HighLowOptions)
            .Should().NotBeNull();

        KeyLevels.ZoneFor(Pivot(100m, prominence: 4m), atr: 10m, HighLowOptions)
            .Should().BeNull("0.4 ATR of clearance is below the 0.5 floor");
    }

    [Fact]
    public void ZoneFor_Refuses_WhenThereIsNoAtrToScaleWith()
    {
        // A missing scale is missing. Sizing a zone against zero — or against a negative — would produce a
        // level whose width and significance are fiction, and it would look exactly like a real one.
        SwingPivot pivot = Pivot(110m, prominence: 4m);

        ((Action)(() => KeyLevels.ZoneFor(pivot, atr: 0m, HighLowOptions)))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*ATR*");

        ((Action)(() => KeyLevels.ZoneFor(pivot, atr: -1m, HighLowOptions)))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*ATR*");
    }

    // ═══ MergeOverlapping ═════════════════════════════════════════════════════════════════════════════

    private static KeyLevelZone Zone(
        decimal bottom,
        decimal top,
        int formedAt,
        int touches,
        decimal significance,
        KeyLevelKind kind = KeyLevelKind.Support) =>
        new(bottom, top, kind, At(formedAt), touches, significance);

    [Fact]
    public void MergeOverlapping_KeepsTheEarliestFormationTheStrongestSignificanceAndTheSumOfTouches()
    {
        // [100,104] formed at bar 1 with 1 touch and 2.0 significance, and [102,106] formed EARLIER at
        // bar 0 with 3 touches and 1.5. They intersect over [102,104], so one zone comes out:
        //   bottom min(100,102) = 100     top max(104,106) = 106
        //   formed min(t1,t0)   = t0      touches 1 + 3    = 4        significance max(2.0,1.5) = 2.0
        //
        // This is the case that goes red if the merge is removed — two zones instead of one — and the one
        // that catches the specific quiet bug of a merge that keeps only the later zone's touch count (3),
        // or only the surviving zone's (1), or the latest formation time (t1).
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 1, touches: 1, significance: 2.0m),
            Zone(bottom: 102, top: 106, formedAt: 0, touches: 3, significance: 1.5m),
        ]);

        merged.Should().Equal(Zone(bottom: 100, top: 106, formedAt: 0, touches: 4, significance: 2.0m));
    }

    [Fact]
    public void MergeOverlapping_TreatsTouchingEdgesAsAnOverlap()
    {
        // [100,104] and [104,108] share exactly the price 104 and nothing else. That counts: the test is
        // "bottom <= other top", not "<". One zone [100,108] with both touches.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
            Zone(bottom: 104, top: 108, formedAt: 1, touches: 1, significance: 1.0m),
        ]);

        merged.Should().Equal(Zone(bottom: 100, top: 108, formedAt: 0, touches: 2, significance: 1.0m));
    }

    [Fact]
    public void MergeOverlapping_LeavesASeparatedPairAlone()
    {
        // One point of clear air between 104 and 105 is enough. Both zones survive with their own touch
        // counts and their own scores — the counterweight to the case above, so a merge that swallowed
        // everything would not pass here.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
            Zone(bottom: 105, top: 108, formedAt: 1, touches: 2, significance: 3.0m),
        ]);

        merged.Should().Equal(
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
            Zone(bottom: 105, top: 108, formedAt: 1, touches: 2, significance: 3.0m));
    }

    [Fact]
    public void MergeOverlapping_AccumulatesTouchesAlongAChainOfThree()
    {
        // [100,104] meets [103,107] meets [106,110]; the first and the last do not touch each other, so the
        // middle zone is what carries the chain. Running the merge left to right:
        //   [100,104] + [103,107] -> [100,107], formed min(t2,t0) = t0, touches 1 + 2 = 3, sig max(1,3) = 3
        //   [100,107] + [106,110] -> [100,110], formed min(t0,t1) = t0, touches 3 + 4 = 7, sig max(3,2) = 3
        // A merge that dropped the earlier zone's touches at each step would report 4, not 7.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 2, touches: 1, significance: 1.0m),
            Zone(bottom: 103, top: 107, formedAt: 0, touches: 2, significance: 3.0m),
            Zone(bottom: 106, top: 110, formedAt: 1, touches: 4, significance: 2.0m),
        ]);

        merged.Should().Equal(Zone(bottom: 100, top: 110, formedAt: 0, touches: 7, significance: 3.0m));
    }

    [Fact]
    public void MergeOverlapping_NeverMergesAcrossKinds_AndOrdersTheResultGloballyByBottom()
    {
        // A support [100,104] and a resistance [102,106] intersect and still come out as two zones, because
        // the merge groups by kind first. (The epic gh#232 plans to change this deliberately; until then
        // this is the tool's contract, and a change to it is a breaking change.)
        //
        // The ordering is over the whole result, not within each kind: the resistance at 90 sorts ahead of
        // the support at 100, which sorts ahead of the resistance at 102. Inputs are given out of order to
        // make sure the ordering is the merge's work and not the caller's.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
            Zone(bottom: 102, top: 106, formedAt: 1, touches: 1, significance: 2.0m, kind: KeyLevelKind.Resistance),
            Zone(bottom: 90, top: 94, formedAt: 2, touches: 1, significance: 3.0m, kind: KeyLevelKind.Resistance),
        ]);

        merged.Should().Equal(
            Zone(bottom: 90, top: 94, formedAt: 2, touches: 1, significance: 3.0m, kind: KeyLevelKind.Resistance),
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
            Zone(bottom: 102, top: 106, formedAt: 1, touches: 1, significance: 2.0m, kind: KeyLevelKind.Resistance));
    }

    [Fact]
    public void MergeOverlapping_ReturnsNothingForAnEmptyInput()
    {
        KeyLevels.MergeOverlapping([]).Should().BeEmpty();
    }

    // ═══ ApplyClose ═══════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ApplyClose_RelabelsAZoneEntirelyBelowTheCloseAsSupport()
    {
        // [90,94] formed as resistance; price now trades at 100, above all of it. Reporting it as
        // resistance would put a ceiling underneath the market. This is the case that goes red if
        // ApplyClose is removed: without it the zone still reads Resistance.
        IReadOnlyList<KeyLevelZone> relabelled = KeyLevels.ApplyClose(
            [Zone(bottom: 90, top: 94, formedAt: 0, touches: 1, significance: 1.0m, kind: KeyLevelKind.Resistance)],
            close: 100m);

        relabelled.Should().ContainSingle().Which.Kind.Should().Be(KeyLevelKind.Support);
    }

    [Fact]
    public void ApplyClose_RelabelsAZoneEntirelyAboveTheCloseAsResistance()
    {
        IReadOnlyList<KeyLevelZone> relabelled = KeyLevels.ApplyClose(
            [Zone(bottom: 110, top: 114, formedAt: 0, touches: 1, significance: 1.0m)],
            close: 100m);

        relabelled.Should().ContainSingle().Which.Kind.Should().Be(KeyLevelKind.Resistance);
    }

    [Fact]
    public void ApplyClose_LeavesAZoneStraddlingTheCloseWithItsFormationKind()
    {
        // Price is inside the zone, so neither reading is right and the formation's own is kept. The two
        // edge cases matter: a top exactly at the close is not "below" it, and a bottom exactly at the
        // close is not "above" it — both are still straddling.
        IReadOnlyList<KeyLevelZone> relabelled = KeyLevels.ApplyClose(
            [
                Zone(bottom: 98, top: 102, formedAt: 0, touches: 1, significance: 1.0m, kind: KeyLevelKind.Resistance),
                Zone(bottom: 96, top: 100, formedAt: 0, touches: 1, significance: 1.0m),
                Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m, kind: KeyLevelKind.Resistance),
            ],
            close: 100m);

        relabelled.Select(z => z.Kind).Should().Equal(
            KeyLevelKind.Resistance,
            KeyLevelKind.Support,
            KeyLevelKind.Resistance);
    }

    [Fact]
    public void ApplyClose_ChangesNothingButTheKind()
    {
        // Bounds, formation time, touch count and score all survive the relabelling untouched, and the
        // zones come back in the order they went in.
        KeyLevelZone below = Zone(bottom: 90, top: 94, formedAt: 3, touches: 5, significance: 2.25m, kind: KeyLevelKind.Resistance);
        KeyLevelZone above = Zone(bottom: 110, top: 114, formedAt: 1, touches: 2, significance: 0.75m);

        IReadOnlyList<KeyLevelZone> relabelled = KeyLevels.ApplyClose([below, above], close: 100m);

        relabelled.Should().Equal(
            below with { Kind = KeyLevelKind.Support },
            above with { Kind = KeyLevelKind.Resistance });
    }

    // ═══ Detect — the whole pipeline ══════════════════════════════════════════════════════════════════

    /// <summary>An ATR series of <paramref name="count"/> identical values, aligned with the bars.</summary>
    private static IReadOnlyList<decimal?> FlatAtr(decimal value, int count) =>
        [.. Enumerable.Repeat<decimal?>(value, count)];

    [Fact]
    public void Detect_ProducesTheHandDerivedZonesForTheWorkedFixture()
    {
        // Fixture A, ATR 4 at every bar, multiple 0.5 (so a half-band of 4 * 0.5 / 2 = 1), floor 0.5.
        //
        //   pivot          significance      zone
        //   (2, 110, R, 4)  4 / 4 = 1.0      [109, 111]
        //   (5,  98, S, 2)  2 / 4 = 0.5      [ 97,  99]   <- exactly on the floor, so it survives
        //   (8, 112, R, 6)  6 / 4 = 1.5      [111, 113]
        //
        //   merge: the two resistances touch at 111 -> [109, 113], formed min(t2,t8) = t2,
        //          touches 1 + 1 = 2, significance max(1.0, 1.5) = 1.5. The support stands alone.
        //   close: bar 10's, 104. [97,99] is wholly below it -> Support. [109,113] wholly above -> Resistance.
        //
        // Remove the merge and this returns three zones with a touch count of 1 each.
        IReadOnlyList<KeyLevelZone> levels =
            KeyLevels.Detect(FixtureA, FlatAtr(4m, 11), HighLowOptions);

        levels.Should().Equal(
            new KeyLevelZone(97m, 99m, KeyLevelKind.Support, At(5), TouchCount: 1, Significance: 0.5m),
            new KeyLevelZone(109m, 113m, KeyLevelKind.Resistance, At(2), TouchCount: 2, Significance: 1.5m));
    }

    [Fact]
    public void Detect_SizesEachZoneWithTheAtrAtItsOwnPivotBar()
    {
        // The same fixture with the ATR raised to 8 at bar 8 alone:
        //   (2, 110, R, 4)  4 / 4 = 1.0     half-band 1  ->  [109, 111]
        //   (5,  98, S, 2)  2 / 4 = 0.5     half-band 1  ->  [ 97,  99]
        //   (8, 112, R, 6)  6 / 8 = 0.75    half-band 2  ->  [110, 114]
        //   merge: [109,111] and [110,114] overlap -> [109, 114], formed t2, touches 2,
        //          significance max(1.0, 0.75) = 1.0
        // Reading the ATR from anywhere else — bar 0, the last bar, a series-wide average — moves that top
        // edge off 114 and the score off 1.0.
        IReadOnlyList<decimal?> atr = [.. FlatAtr(4m, 11).Select((v, i) => i == 8 ? 8m : v)];

        IReadOnlyList<KeyLevelZone> levels = KeyLevels.Detect(FixtureA, atr, HighLowOptions);

        levels.Should().Equal(
            new KeyLevelZone(97m, 99m, KeyLevelKind.Support, At(5), TouchCount: 1, Significance: 0.5m),
            new KeyLevelZone(109m, 114m, KeyLevelKind.Resistance, At(2), TouchCount: 2, Significance: 1.0m));
    }

    [Fact]
    public void Detect_RelabelsAnOldResistanceAsSupportOnceTheCloseIsAboveIt()
    {
        // Fixture B, ATR 4 everywhere. Two pivots, both formed early:
        //   (2, 110, R, 4)  4 / 4 = 1.0   -> [109, 111]
        //   (5,  98, S, 2)  2 / 4 = 0.5   -> [ 97,  99]
        // Nothing merges — different kinds, and they do not touch anyway. The last close is 130, above
        // both, so BOTH come back as support. Remove ApplyClose and the 109-111 zone is still reported as
        // resistance twenty points under the market.
        IReadOnlyList<KeyLevelZone> levels =
            KeyLevels.Detect(FixtureB, FlatAtr(4m, 11), HighLowOptions);

        levels.Should().Equal(
            new KeyLevelZone(97m, 99m, KeyLevelKind.Support, At(5), TouchCount: 1, Significance: 0.5m),
            new KeyLevelZone(109m, 111m, KeyLevelKind.Support, At(2), TouchCount: 1, Significance: 1.0m));
    }

    [Fact]
    public void Detect_SkipsAPivotWithNoAtrRatherThanSubstitutingOne()
    {
        // Bar 2's ATR is absent, so the 110 pivot has no scale to be sized or scored with and no level
        // comes out of it. The remaining two are unaffected — and, with the 109-111 zone gone, the bar 8
        // zone is [111, 113] on its own rather than merged into [109, 113]. A substituted default would
        // have produced a level whose width and significance are invented.
        IReadOnlyList<decimal?> atr = [.. FlatAtr(4m, 11).Select((v, i) => i == 2 ? (decimal?)null : v)];

        IReadOnlyList<KeyLevelZone> levels = KeyLevels.Detect(FixtureA, atr, HighLowOptions);

        levels.Should().Equal(
            new KeyLevelZone(97m, 99m, KeyLevelKind.Support, At(5), TouchCount: 1, Significance: 0.5m),
            new KeyLevelZone(111m, 113m, KeyLevelKind.Resistance, At(8), TouchCount: 1, Significance: 1.5m));
    }

    [Fact]
    public void Detect_ReportsNoLevelsAtAll_WhenTheWholeAtrSeriesIsMissing()
    {
        // The pivots are all still there; what is missing is the only thing that can size them. Three
        // levels of unknown width is not a better answer than none.
        IReadOnlyList<decimal?> nothing = [.. Enumerable.Repeat<decimal?>(null, 11)];

        KeyLevels.FindPivots(FixtureA, HighLowOptions).Should().HaveCount(3);
        KeyLevels.Detect(FixtureA, nothing, HighLowOptions).Should().BeEmpty();
    }

    [Fact]
    public void Detect_Refuses_WhenTheAtrSeriesIsNotAlignedWithTheBars()
    {
        Action detect = () => KeyLevels.Detect(FixtureA, FlatAtr(4m, 10), HighLowOptions);

        detect.Should().Throw<ArgumentException>().WithMessage("*align*");
    }

    [Fact]
    public void Detect_Refuses_WhenTwoBarsAreTransposed()
    {
        // Detect reaches the ordering guard only through FindPivots, and Detect is the entry point the tool
        // calls -- so the refusal is pinned at the surface a caller actually holds, not just at the stage
        // that happens to check today.
        //
        // Pinned separately rather than assumed from the FindPivots case, because Detect does its own work
        // before delegating: it validates the ATR alignment and short-circuits an empty series first. Either
        // of those growing a path that answers before FindPivots is reached would drop the refusal here
        // while leaving the FindPivots case green.
        Action detect = () => KeyLevels.Detect(FixtureATransposed(4, 5), FlatAtr(4m, 11), HighLowOptions);

        detect.Should().Throw<ArgumentException>()
            .WithMessage("*strictly ascending*")
            .WithParameterName("bars");
    }

    [Fact]
    public void Detect_ReportsNothingForAnEmptySeries()
    {
        KeyLevels.Detect([], [], HighLowOptions).Should().BeEmpty();
    }

    // ═══ Reproducibility — ADR-0006 ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Detect_YieldsIdenticalZones_WhenRecomputedOverTheSameBars()
    {
        // Recomputing over the same bars must give the same numbers, so a level an agent was shown can be
        // rebuilt exactly from the bars that were on hand. The run with different options in the middle is
        // deliberate: a cached or otherwise carried-over result would survive two identical calls in a row
        // and not survive being interleaved.
        IReadOnlyList<decimal?> atr = FlatAtr(4m, 11);

        IReadOnlyList<KeyLevelZone> first = KeyLevels.Detect(FixtureA, atr, HighLowOptions);
        _ = KeyLevels.Detect(FixtureB, atr, HighLowOptions with { Lookback = 3, ZoneAtrMultiple = 1.5m });
        IReadOnlyList<KeyLevelZone> second = KeyLevels.Detect(FixtureA, atr, HighLowOptions);

        second.Should().Equal(first);
        second.Should().Equal(KeyLevels.Detect([.. FixtureA], FlatAtr(4m, 11), HighLowOptions));
    }

    [Fact]
    public void Detect_DatesAZoneFromTheBarThatFormedIt_NeverFromAClock()
    {
        // FormedAtBucket is the pivot bar's own open time and nothing else. Every one of these is a fixed
        // 2026-08-18 bucket that came in with the fixture; a Domain that read a clock could not produce
        // them, and would drift on every run.
        IReadOnlyList<KeyLevelZone> levels = KeyLevels.Detect(FixtureA, FlatAtr(4m, 11), HighLowOptions);

        levels.Select(z => z.FormedAtBucket).Should().Equal(At(5), At(2));
        levels.Should().OnlyContain(z => z.FormedAtBucket.Offset == TimeSpan.Zero);
    }

    [Fact]
    public void Detect_IsUnaffectedByTheAmbientCulture()
    {
        // A culture whose decimal separator is a comma changes nothing: no stage parses or formats a price,
        // and none reads a configuration singleton to decide how to.
        IReadOnlyList<KeyLevelZone> invariant =
            KeyLevels.Detect(FixtureA, FlatAtr(4m, 11), HighLowOptions);

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            KeyLevels.Detect(FixtureA, FlatAtr(4m, 11), HighLowOptions).Should().Equal(invariant);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
