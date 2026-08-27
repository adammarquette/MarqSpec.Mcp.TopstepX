using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The four behaviours gh#245 adopted from Bjorgum's <i>Key Levels</i> — cross-kind merge, asymmetric
/// lookback, a percentage cap on zone width, and a cap on how many levels come back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each of the four is pinned on its own stage, so removing one reddens its own cases and no others.</b>
/// The cross-kind cases enter at <see cref="KeyLevels.MergeOverlapping"/> and never mention a lookback or a
/// cap; the lookback cases enter at <see cref="KeyLevels.FindPivots"/> and produce no zones at all; the two
/// cap cases enter at <see cref="KeyLevels.ApplyWidthCap"/> and <see cref="KeyLevels.ApplyLevelCap"/> with
/// zones handed straight in. There is exactly one case that crosses two behaviours on purpose —
/// <see cref="ApplyWidthCap_FiresOnWhatTheMergeProduced_NotOnWhatWentIntoIt"/> — because "the cap is
/// downstream of the merge" is a claim about the two together and cannot be shown by either alone.
/// </para>
/// <para>
/// <b>Every expected number here was worked out from the definition before the behaviour existed, and the
/// derivation is written beside it.</b> The same rule <c>KeyLevelsTests</c> opens with: a fixture captured
/// from a run pins whatever the run did, including a bug, and then defends it. Every denominator is 2, 4 or
/// 100, so every value is exact in <see langword="decimal"/> and none of these comparisons is approximate.
/// </para>
/// <para>
/// <b>Both caps drop, and dropping is the whole point.</b> A capped-away level is <i>absent</i> — not
/// narrowed to the cap, not folded into the survivor next to it. Either of those reports a band at a price
/// nothing was measured at, which is the same failure as filling a missing indicator with a neutral value.
/// The cases below assert the survivors as <b>whole records</b> for exactly that reason: a zone that had
/// absorbed a dropped neighbour would carry its touches or its bounds, and record equality catches that
/// where a count would not.
/// </para>
/// </remarks>
public sealed class KeyLevelBjorgumBehaviourTests
{
    /// <summary>The open time of bar <paramref name="index"/> — five-minute buckets from a fixed origin.</summary>
    private static DateTimeOffset At(int index) =>
        new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero).AddMinutes(5 * index);

    /// <summary>A bar written as just its high and its low, opening at the low and closing at the high.</summary>
    private static Bar HighLowBar(int index, decimal high, decimal low) =>
        new(At(index), Open: low, High: high, Low: low, Close: high, Volume: 100);

    /// <summary>A zone, written the way the derivations above each case write one.</summary>
    private static KeyLevelZone Zone(
        decimal bottom,
        decimal top,
        int formedAt,
        int touches,
        decimal significance,
        KeyLevelKind kind = KeyLevelKind.Support) =>
        new(bottom, top, kind, At(formedAt), touches, significance);

    /// <summary>An ATR series of <paramref name="count"/> identical values, aligned with the bars.</summary>
    private static IReadOnlyList<decimal?> FlatAtr(decimal value, int count) =>
        [.. Enumerable.Repeat<decimal?>(value, count)];

    // ═══ 1 · CROSS-KIND MERGE ═════════════════════════════════════════════════════════════════════════
    //
    //  Until gh#245 the merge grouped by kind first, so a support and a resistance that occupied the same
    //  prices came back as two zones sitting on top of each other. They now merge. Everything else the merge
    //  does is unchanged and is pinned by KeyLevelsTests; these cases add only the part that crosses kinds.

    [Fact]
    public void MergeOverlapping_MergesASupportIntoAnOverlappingResistance()
    {
        // A support [100,104] formed at bar 1 with 1 touch and 1.0 significance, and a resistance [102,106]
        // formed at bar 0 with 3 touches and 2.0. They intersect over [102,104], and the kinds no longer
        // keep them apart:
        //   bottom min(100,102) = 100     top max(104,106) = 106
        //   formed min(t1,t0)   = t0      touches 1 + 3    = 4      significance max(1.0,2.0) = 2.0
        //   kind   the STRONGER constituent's, which is the resistance at 2.0
        //
        // This is the case that goes red if cross-kind merging is removed: two zones come back instead of
        // one, at the bounds they arrived with.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 1, touches: 1, significance: 1.0m),
            Zone(bottom: 102, top: 106, formedAt: 0, touches: 3, significance: 2.0m, kind: KeyLevelKind.Resistance),
        ]);

        merged.Should().Equal(
            Zone(bottom: 100, top: 106, formedAt: 0, touches: 4, significance: 2.0m, kind: KeyLevelKind.Resistance));
    }

    [Fact]
    public void MergeOverlapping_TakesItsKindFromTheStrongestConstituent()
    {
        // The mirror of the case above, with the two significances swapped and nothing else changed. The
        // support is now the 3.0 and the resistance the 2.0, so the merged zone comes back SUPPORT over the
        // same bounds:
        //   bottom 100   top 106   formed min(t0,t1) = t0   touches 2   significance max(3.0,2.0) = 3.0
        //
        // A merge that took the kind from whichever zone it happened to open with — the lower one, which is
        // the support in both cases — would pass the case above and this one, and would then disagree with
        // itself the moment the input order changed. The pair is what makes the rule falsifiable, and
        // MergeOverlapping_ResolvesACrossKindTieWithoutRegardToInputOrder closes the order question.
        IReadOnlyList<KeyLevelZone> merged = KeyLevels.MergeOverlapping(
        [
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 3.0m),
            Zone(bottom: 102, top: 106, formedAt: 1, touches: 1, significance: 2.0m, kind: KeyLevelKind.Resistance),
        ]);

        merged.Should().Equal(
            Zone(bottom: 100, top: 106, formedAt: 0, touches: 2, significance: 3.0m));
    }

    [Fact]
    public void MergeOverlapping_ResolvesACrossKindTieWithoutRegardToInputOrder()
    {
        // Both constituents score 2.0, so significance cannot separate them and the earlier formation does:
        // the resistance formed at bar 0, the support at bar 1, so the merged zone is a RESISTANCE.
        //   bottom 100   top 106   formed t0   touches 2   significance 2.0
        //
        // Asserted twice over the same two zones handed in opposite orders. Reproducibility is not a nicety
        // here — ADR-0013 lets detection parameters be per-call precisely because a level set is a pure
        // function of the bars and these options, and a kind that depended on enumeration order would make
        // two identical requests disagree with nothing to say which was right.
        KeyLevelZone support = Zone(bottom: 100, top: 104, formedAt: 1, touches: 1, significance: 2.0m);
        KeyLevelZone resistance =
            Zone(bottom: 102, top: 106, formedAt: 0, touches: 1, significance: 2.0m, kind: KeyLevelKind.Resistance);

        KeyLevelZone expected =
            Zone(bottom: 100, top: 106, formedAt: 0, touches: 2, significance: 2.0m, kind: KeyLevelKind.Resistance);

        KeyLevels.MergeOverlapping([support, resistance]).Should().Equal(expected);
        KeyLevels.MergeOverlapping([resistance, support]).Should().Equal(expected);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  FIXTURE C — a support and a resistance that overlap, through the whole pipeline. Seven bars, read
    //  High/Low, lookback 1 either side, ATR 2 at every bar.
    //
    //    i:      0      1      2      3      4      5      6
    //    high: 105    105    104    103   99.6  100.8   99.6
    //    low:  102    100    102    101   99.0   99.5   99.0
    //
    //  A pivot needs `i` in [1, 7 - 1) = 1..5, and must strictly dominate one bar either side:
    //
    //    i=1  high 105 TIES bar 0's 105 -> not a high.  low 100 vs {102,102} -> dominates.
    //         Prominence 102 - 100 = 2. Support at 100.
    //    i=2  high 104 under bar 1's 105 -> no.   low 102 over bar 1's 100 -> no.
    //    i=3  high 103 under bar 2's 104 -> no.   low 101 over bar 4's 99.0 -> no.
    //    i=4  high 99.6 under bar 3's 103 -> no.  low 99.0 vs {101, 99.5} -> dominates.
    //         Prominence 99.5 - 99.0 = 0.5, which scores 0.25 and dies under the 0.5 floor.
    //    i=5  high 100.8 vs {99.6, 99.6} -> dominates. Prominence 100.8 - 99.6 = 1.2. Resistance at 100.8.
    //
    //  Two zones survive the floor, with a half-band of 2 x 0.5 / 2 = 0.5:
    //
    //    support    100  -> [ 99.5, 100.5]  significance 2   / 2 = 1.0   formed at bar 1
    //    resistance 100.8 -> [100.3, 101.3] significance 1.2 / 2 = 0.6   formed at bar 5
    //
    //  They intersect over [100.3, 100.5]. Cross-kind merging is the only reason that matters.
    //  The last close is bar 6's, which is its high, 99.6 — INSIDE the merged zone, which is the one case
    //  where ApplyClose leaves the formation's own reading alone and the merged kind is what is reported.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> FixtureC =>
    [
        HighLowBar(0, high: 105m, low: 102m),
        HighLowBar(1, high: 105m, low: 100m),
        HighLowBar(2, high: 104m, low: 102m),
        HighLowBar(3, high: 103m, low: 101m),
        HighLowBar(4, high: 99.6m, low: 99.0m),
        HighLowBar(5, high: 100.8m, low: 99.5m),
        HighLowBar(6, high: 99.6m, low: 99.0m),
    ];

    /// <summary>Fixture C's options — the caps wide enough that neither is what this case is about.</summary>
    private static KeyLevelOptions FixtureCOptions => new(
        Lookback: 1,
        Source: PivotSource.HighLow,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m,
        RightLookback: 1,
        MaxZoneWidthPercent: 100m,
        MaxLevels: 1_000);

    [Fact]
    public void Detect_ReportsAnOverlappingSupportAndResistanceAsOneZone()
    {
        // The derivation is beside fixture C. One zone comes out:
        //   bottom min(99.5, 100.3) = 99.5     top max(100.5, 101.3) = 101.3
        //   formed min(t1, t5)      = t1       touches 1 + 1         = 2
        //   significance max(1.0, 0.6) = 1.0   kind: the 1.0 is the support, so SUPPORT
        //   close 99.6 is inside [99.5, 101.3], so ApplyClose keeps the formation's reading
        //
        // Before gh#245 this same fixture returned TWO zones — [99.5,100.5] support and [100.3,101.3]
        // resistance, each with one touch — which is the breaking change to `get_key_levels` stated as a
        // number. That pair is not asserted here because it no longer exists to assert; it was measured on
        // this branch before the merge was changed, and it is tabled in the pull request.
        IReadOnlyList<KeyLevelZone> levels = KeyLevels.Detect(FixtureC, FlatAtr(2m, 7), FixtureCOptions);

        levels.Should().Equal(
            new KeyLevelZone(99.5m, 101.3m, KeyLevelKind.Support, At(1), TouchCount: 2, Significance: 1.0m));
    }

    // ═══ 2 · ASYMMETRIC LOOKBACK ══════════════════════════════════════════════════════════════════════
    //
    //  FIXTURE L — one series, three windows, three different answers. Eleven bars, read High/Low.
    //
    //    i:      0     1     2     3     4     5     6     7     8     9    10
    //    high: 100   101   102   110   103   104   110   105   109   106   107
    //    low:   90    91    92    93    94    95    96    97    98    99   100
    //
    //  The lows rise by one every bar, so no bar's low is ever under its left neighbour's and NO low can be
    //  a pivot under any window. Everything below is about the highs, which is what makes it checkable.
    //
    //  Under 3 left / 1 right — the shape gh#245 adopted — `i` runs over [3, 11 - 1) = 3..9:
    //
    //    i=3  110 vs left {100,101,102} and right {103}  -> dominates. Prominence 110 - 103 = 7.
    //    i=4  103 vs left {101,102,110} -> 110 is higher.
    //    i=5  104 vs left {102,110,103} -> 110 is higher.
    //    i=6  110 vs left {110,103,104} -> TIES bar 3, and a tie is not dominance.
    //    i=7  105 vs left {103,104,110} -> 110 is higher.
    //    i=8  109 vs left {104,110,105} -> 110 is higher.
    //    i=9  106 vs left {110,105,109} -> 110 is higher.
    //
    //  ONE pivot, at bar 3.
    //
    //  Under 3 left / 3 right — what the pipeline did before, and what it still does if RightLookback is
    //  ignored — `i` runs over [3, 8):
    //
    //    i=3  right window now reaches bar 6, whose 110 ties it -> gone.
    //    i=4..7  each has bar 3's or bar 6's 110 inside its left window.
    //
    //  NO pivots at all.
    //
    //  Under 1 left / 1 right — what it does if the LEFT lookback is ignored instead — `i` runs over [1,10):
    //
    //    i=3  110 vs {102,103} -> dominates. Prominence 7.
    //    i=6  110 vs {104,105} -> dominates. Prominence 110 - 105 = 5.
    //    i=8  109 vs {105,106} -> dominates. Prominence 109 - 106 = 3.
    //
    //  THREE pivots. So the two edges of the window are separately falsifiable: collapse the right onto the
    //  left and the answer empties, collapse the left onto the right and it triples.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> FixtureL =>
    [
        HighLowBar(0, high: 100, low: 90),
        HighLowBar(1, high: 101, low: 91),
        HighLowBar(2, high: 102, low: 92),
        HighLowBar(3, high: 110, low: 93),
        HighLowBar(4, high: 103, low: 94),
        HighLowBar(5, high: 104, low: 95),
        HighLowBar(6, high: 110, low: 96),
        HighLowBar(7, high: 105, low: 97),
        HighLowBar(8, high: 109, low: 98),
        HighLowBar(9, high: 106, low: 99),
        HighLowBar(10, high: 107, low: 100),
    ];

    /// <summary>Fixture L's options at a stated left and right window.</summary>
    private static KeyLevelOptions Window(int left, int right) => new(
        Lookback: left,
        Source: PivotSource.HighLow,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m,
        RightLookback: right,
        MaxZoneWidthPercent: 100m,
        MaxLevels: 1_000);

    [Fact]
    public void FindPivots_DominatesTheLeftAndRightWindowsSeparately()
    {
        // The derivation for all three windows is beside fixture L. All three are asserted, not just the
        // one that describes the new behaviour: an implementation that read RightLookback and ignored
        // Lookback would satisfy the first assertion on its own.
        KeyLevels.FindPivots(FixtureL, Window(left: 3, right: 1)).Should().Equal(
            new SwingPivot(BarIndex: 3, At(3), Price: 110m, KeyLevelKind.Resistance, Prominence: 7m));

        KeyLevels.FindPivots(FixtureL, Window(left: 3, right: 3)).Should().BeEmpty(
            "bar 6's 110 sits inside bar 3's right window once that window is three wide, and a tie is not "
            + "dominance");

        KeyLevels.FindPivots(FixtureL, Window(left: 1, right: 1)).Should().Equal(
            new SwingPivot(BarIndex: 3, At(3), Price: 110m, KeyLevelKind.Resistance, Prominence: 7m),
            new SwingPivot(BarIndex: 6, At(6), Price: 110m, KeyLevelKind.Resistance, Prominence: 5m),
            new SwingPivot(BarIndex: 8, At(8), Price: 109m, KeyLevelKind.Resistance, Prominence: 3m));
    }

    [Fact]
    public void FindPivots_NeedsTheTwoWindowsAndOneBarBetweenThem()
    {
        // FIXTURE M — six bars, the shortest series a 3-left/2-right window can hold a pivot in.
        //
        //   i:      0     1     2     3     4     5
        //   high: 100   101   102   110   103   104
        //   low:   90    91    92    93    94    95
        //
        // The floor is left + right + 1 = 6, so `i` runs over [3, 6 - 2) — index 3 and nothing else. Bar 3's
        // 110 dominates all five others; the highest of them is bar 5's 104, so the prominence is 6.
        //
        // The floor that was here before gh#245 was 2 x lookback + 1, which on a left window of 3 is SEVEN.
        // Under it these six bars produce nothing, so that reading is what this case falsifies — and the
        // five-bar half below is the counterweight, because a floor removed altogether would pass the first
        // assertion and let a five-bar series index past its own end.
        IReadOnlyList<Bar> six =
        [
            HighLowBar(0, high: 100, low: 90),
            HighLowBar(1, high: 101, low: 91),
            HighLowBar(2, high: 102, low: 92),
            HighLowBar(3, high: 110, low: 93),
            HighLowBar(4, high: 103, low: 94),
            HighLowBar(5, high: 104, low: 95),
        ];

        KeyLevels.FindPivots(six, Window(left: 3, right: 2)).Should().Equal(
            new SwingPivot(BarIndex: 3, At(3), Price: 110m, KeyLevelKind.Resistance, Prominence: 6m));

        KeyLevels.FindPivots([.. six.Take(5)], Window(left: 3, right: 2)).Should().BeEmpty(
            "five bars cannot hold a window of three, one and two");
    }

    [Fact]
    public void FindPivots_Refuses_WhenTheRightWindowIsBelowOne()
    {
        // Zero is not "no confirmation required" — it is R-3.4 turned off. A candidate judged only against
        // the bars before it repaints as soon as the next one arrives, into a level an agent has already
        // been shown, so the floor is one on both edges rather than only on the left.
        ((Action)(() => KeyLevels.FindPivots(FixtureL, Window(left: 3, right: 0))))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*right*");
    }

    // ═══ 3 · THE WIDTH CAP ════════════════════════════════════════════════════════════════════════════
    //
    //  A zone survives while its width is at most MaxZoneWidthPercent of its own midpoint. The comparison
    //  is written as width x 100 <= cap x midpoint rather than as a division, so every case below is exact
    //  in decimal and none of them needs a tolerance.

    /// <summary>Options carrying a stated width cap and nothing else that could interfere.</summary>
    private static KeyLevelOptions WidthCap(decimal percent) => new(
        Lookback: 1,
        Source: PivotSource.HighLow,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m,
        RightLookback: 1,
        MaxZoneWidthPercent: percent,
        MaxLevels: 1_000);

    [Fact]
    public void ApplyWidthCap_DropsTheZonesOverTheCapAndLeavesTheRestUntouched()
    {
        // Cap 5%. Worked against each zone's own midpoint, because a percentage of the instrument is what
        // makes a width mean the same thing on ES and on NQ:
        //   [100,104]  width  4  midpoint 102   4 x 100 =  400 <=  5 x 102 =  510   keep   (3.92%)
        //   [200,220]  width 20  midpoint 210  20 x 100 = 2000 >   5 x 210 = 1050   DROP   (9.52%)
        //   [295,305]  width 10  midpoint 300  10 x 100 = 1000 <=  5 x 300 = 1500   keep   (3.33%)
        //
        // The survivors are asserted as whole records: a cap that narrowed the offender to 5% instead of
        // dropping it, or folded its touches into a neighbour, would still leave two zones behind.
        IReadOnlyList<KeyLevelZone> kept = KeyLevels.ApplyWidthCap(
            [
                Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
                Zone(bottom: 200, top: 220, formedAt: 1, touches: 5, significance: 4.0m),
                Zone(bottom: 295, top: 305, formedAt: 2, touches: 2, significance: 2.0m),
            ],
            WidthCap(5m));

        kept.Should().Equal(
            Zone(bottom: 100, top: 104, formedAt: 0, touches: 1, significance: 1.0m),
            Zone(bottom: 295, top: 305, formedAt: 2, touches: 2, significance: 2.0m));
    }

    [Fact]
    public void ApplyWidthCap_KeepsAZoneSittingExactlyOnTheCap()
    {
        // [195,205] is width 10 on a midpoint of 200, which is 5% to the digit: 10 x 100 = 1000 and
        // 5 x 200 = 1000. The test is "at most", so it survives. Its neighbour is a fifth of a point wider
        // on the same midpoint — [194.9, 205.1], width 10.2, 1020 > 1000 — and does not. The boundary is
        // pinned from both sides because an off-by-one there is invisible in any other case.
        IReadOnlyList<KeyLevelZone> kept = KeyLevels.ApplyWidthCap(
            [
                Zone(bottom: 195m, top: 205m, formedAt: 0, touches: 1, significance: 1.0m),
                Zone(bottom: 194.9m, top: 205.1m, formedAt: 1, touches: 1, significance: 1.0m),
            ],
            WidthCap(5m));

        kept.Should().Equal(Zone(bottom: 195m, top: 205m, formedAt: 0, touches: 1, significance: 1.0m));
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  FIXTURE W — three pivots close enough to chain into one wide zone. Seven bars, High/Low, lookback 1
    //  either side, ATR 4 at every bar, so a half-band of 4 x 0.5 / 2 = 1 and every zone two points wide.
    //
    //    i:      0     1      2     3      4     5      6
    //    high:  90   100     95   101.5   96   103     91
    //    low:   80    81     82    83     84    85     86
    //
    //  The lows rise by one every bar, so again no low can be a pivot. `i` runs over [1, 6):
    //
    //    i=1  100   vs {90, 95}      -> dominates. Prominence 100   - 95 = 5.0   significance 1.25
    //    i=2   95   vs {100, 101.5}  -> no.
    //    i=3  101.5 vs {95, 96}      -> dominates. Prominence 101.5 - 96 = 5.5   significance 1.375
    //    i=4   96   vs {101.5, 103}  -> no.
    //    i=5  103   vs {96, 91}      -> dominates. Prominence 103   - 96 = 7.0   significance 1.75
    //
    //  Three zones, each two points wide:  [99,101]  [100.5,102.5]  [102,104]
    //  They chain: [99,101] meets [100.5,102.5] meets [102,104] -> one zone [99, 104].
    //
    //    merged width 5 on a midpoint of 101.5.
    //    Against the SHIPPED 2.5% cap:  5 x 100 = 500  >  2.5 x 101.5 = 253.75   ->  dropped.
    //    Every constituent against the SAME cap:
    //      [99,101]      width 2  midpoint 100    200 <= 2.5 x 100   = 250       ->  kept
    //      [100.5,102.5] width 2  midpoint 101.5  200 <= 2.5 x 101.5 = 253.75    ->  kept
    //      [102,104]     width 2  midpoint 103    200 <= 2.5 x 103   = 257.5     ->  kept
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> FixtureW =>
    [
        HighLowBar(0, high: 90m, low: 80m),
        HighLowBar(1, high: 100m, low: 81m),
        HighLowBar(2, high: 95m, low: 82m),
        HighLowBar(3, high: 101.5m, low: 83m),
        HighLowBar(4, high: 96m, low: 84m),
        HighLowBar(5, high: 103m, low: 85m),
        HighLowBar(6, high: 91m, low: 86m),
    ];

    [Fact]
    public void ApplyWidthCap_FiresOnWhatTheMergeProduced_NotOnWhatWentIntoIt()
    {
        // The one case here that is deliberately about two behaviours at once, because the claim is about
        // the order they run in. The three pre-merge zones are each 2 points wide and each clears the
        // shipped 2.5% cap; the zone they chain into is 5 points wide and does not. So the same cap, over
        // the same bars, keeps everything before the merge and nothing after it — which is the measurement
        // behind "the merge is the only stage that can widen a zone without limit, and the cap belongs
        // downstream of it". Both halves are computed below rather than asserted from the comment.
        IReadOnlyList<KeyLevelZone> beforeTheMerge =
        [
            .. KeyLevels.FindPivots(FixtureW, WidthCap(2.5m))
                .Select(pivot => KeyLevels.ZoneFor(pivot, atr: 4m, WidthCap(2.5m)))
                .Where(zone => zone is not null)
                .Select(zone => zone!),
        ];

        beforeTheMerge.Should().HaveCount(3, "the fixture's three pivots all clear the significance floor");
        KeyLevels.ApplyWidthCap(beforeTheMerge, WidthCap(2.5m)).Should().Equal(beforeTheMerge);

        KeyLevels.MergeOverlapping(beforeTheMerge).Should().ContainSingle()
            .Which.Should().Be(
                new KeyLevelZone(99m, 104m, KeyLevelKind.Resistance, At(1), TouchCount: 3, Significance: 1.75m));

        KeyLevels.Detect(FixtureW, FlatAtr(4m, 7), WidthCap(2.5m)).Should().BeEmpty(
            "the merged zone is 5 points wide on a midpoint of 101.5, which is 4.926% and over the cap");

        // The counterweight, so the case above is not merely a cap that rejects everything: widen the cap
        // past 4.926% and the same bars produce the merged zone, labelled against a close of 91 that sits
        // below it.
        KeyLevels.Detect(FixtureW, FlatAtr(4m, 7), WidthCap(5m)).Should().Equal(
            new KeyLevelZone(99m, 104m, KeyLevelKind.Resistance, At(1), TouchCount: 3, Significance: 1.75m));
    }

    // ═══ 4 · THE LEVEL CAP ════════════════════════════════════════════════════════════════════════════
    //
    //  At most MaxLevels come back, and the ones kept are the most significant. Significance is prominence
    //  in ATR multiples (R-3.2) — the one score this server already treats as comparable across instruments
    //  — so ranking by it is ranking by the same thing on ES and on NQ.

    /// <summary>Options carrying a stated level cap and nothing else that could interfere.</summary>
    private static KeyLevelOptions LevelCap(int levels) => new(
        Lookback: 1,
        Source: PivotSource.HighLow,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m,
        RightLookback: 1,
        MaxZoneWidthPercent: 100m,
        MaxLevels: levels);

    /// <summary>Five separated zones at rising prices, scored out of order.</summary>
    /// <remarks>
    /// No two overlap, so nothing here can merge and the cap is the only thing that removes anything. The
    /// significances rise and fall against the prices on purpose: a cap that kept the first three, or the
    /// last three, or the three nearest the middle would agree with a ranked cap on some orderings.
    /// </remarks>
    private static IReadOnlyList<KeyLevelZone> FiveZones =>
    [
        Zone(bottom: 100, top: 102, formedAt: 0, touches: 1, significance: 1.0m),
        Zone(bottom: 110, top: 112, formedAt: 1, touches: 2, significance: 5.0m),
        Zone(bottom: 120, top: 122, formedAt: 2, touches: 3, significance: 2.0m),
        Zone(bottom: 130, top: 132, formedAt: 3, touches: 4, significance: 4.0m),
        Zone(bottom: 140, top: 142, formedAt: 4, touches: 5, significance: 3.0m),
    ];

    [Fact]
    public void ApplyLevelCap_KeepsTheMostSignificantAndHandsThemBackInPriceOrder()
    {
        // Cap 3 over the five. Ranked by significance: 5.0 (bottom 110), 4.0 (130), 3.0 (140), then 2.0
        // (120) and 1.0 (100), which are the two that go. The survivors come back in PRICE order, because
        // that is the order `get_key_levels` has always reported and the cap is not a re-ordering.
        IReadOnlyList<KeyLevelZone> kept = KeyLevels.ApplyLevelCap(FiveZones, LevelCap(3));

        kept.Should().Equal(
            Zone(bottom: 110, top: 112, formedAt: 1, touches: 2, significance: 5.0m),
            Zone(bottom: 130, top: 132, formedAt: 3, touches: 4, significance: 4.0m),
            Zone(bottom: 140, top: 142, formedAt: 4, touches: 5, significance: 3.0m));

        // The dropped two are ABSENT, not redistributed. Their four touches are not in the survivors, and
        // no survivor's bounds have moved to cover the ground they held.
        kept.Sum(zone => zone.TouchCount).Should().Be(11);
    }

    [Fact]
    public void ApplyLevelCap_ChangesNothing_WhenThereAreFewerZonesThanTheCap()
    {
        // The counterweight. A cap of ten over five zones is not an occasion to reorder, rescore or trim
        // anything, and the five come back exactly as they arrived.
        KeyLevels.ApplyLevelCap(FiveZones, LevelCap(10)).Should().Equal(FiveZones);
        KeyLevels.ApplyLevelCap(FiveZones, LevelCap(5)).Should().Equal(FiveZones);
    }

    [Fact]
    public void ApplyLevelCap_BreaksASignificanceTieOnTouchesAndThenOnPrice()
    {
        // Three zones tied at 2.0 and a cap of two. Touches separate the first pair — 4 beats 1 — and the
        // remaining tie at 4 touches goes to the lower price. Handed in both orders, because a cap that
        // fell back on enumeration order would answer two identical requests differently.
        KeyLevelZone low = Zone(bottom: 100, top: 102, formedAt: 0, touches: 4, significance: 2.0m);
        KeyLevelZone high = Zone(bottom: 200, top: 202, formedAt: 1, touches: 4, significance: 2.0m);
        KeyLevelZone thin = Zone(bottom: 150, top: 152, formedAt: 2, touches: 1, significance: 2.0m);

        KeyLevels.ApplyLevelCap([low, high, thin], LevelCap(2)).Should().Equal(low, high);
        KeyLevels.ApplyLevelCap([thin, high, low], LevelCap(2)).Should().Equal(low, high);
    }

    [Fact]
    public void Detect_Refuses_WhenTheLevelCapIsBelowOne()
    {
        // A cap of zero empties every level set the server can produce, and an empty level set is what a
        // market with no structure looks like. It is refused at the same place an unset pivot source is,
        // and before the empty-series exit, so an operator who sets it finds out at the first call rather
        // than at the first call that had bars.
        ((Action)(() => KeyLevels.Detect(FixtureL, FlatAtr(4m, 11), LevelCap(0))))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*level cap*");

        ((Action)(() => KeyLevels.Detect([], [], LevelCap(0))))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*level cap*");
    }

    [Fact]
    public void Detect_AppliesTheLevelCapToWhatTheCloseHasAlreadyLabelled()
    {
        // Fixture L under a 1/1 window and an ATR of 4 produces three zones — the derivation is beside the
        // fixture, and the half-band is 1:
        //   bar 3  110 prominence 7  significance 1.75  ->  [109, 111]
        //   bar 6  110 prominence 5  significance 1.25  ->  [109, 111]  <- the same bounds, so these merge
        //   bar 8  109 prominence 3  significance 0.75  ->  [108, 110]  <- and this one overlaps both
        // All three chain into [108, 111]: formed at bar 3, 3 touches, significance max = 1.75. One zone,
        // so a cap of 1 cannot be told from a cap of 3 on these bars — which is why the cap's own ranking
        // is pinned on ApplyLevelCap above and this case pins only that the pipeline runs it last.
        //
        // The last close is bar 10's, 107, below the zone, so it is reported as resistance. Ordering the
        // cap after ApplyClose is safe precisely because ApplyClose changes nothing but the kind, which
        // KeyLevelsTests.ApplyClose_ChangesNothingButTheKind is the measurement of.
        KeyLevels.Detect(FixtureL, FlatAtr(4m, 11), LevelCap(1)).Should().Equal(
            new KeyLevelZone(108m, 111m, KeyLevelKind.Resistance, At(3), TouchCount: 3, Significance: 1.75m));
    }
}
