using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The <c>pivot-*</c> family — five published formulas over one finished session's open, high, low and close.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every number below is hand-derived from the published formula</b>, in the shape gh#242 established for
/// <c>swing</c> and gh#257 repeated for <c>session</c>: the fixture is a table, the period's four prices are
/// read off it by eye, and the arithmetic is worked through in the comment beside the assertion. Nothing here
/// round-trips the implementation. These formulas are published and checkable, so a structural assertion —
/// <i>some levels came back</i> — would be a test that passes forever whatever the arithmetic does.
/// </para>
/// <para>
/// <b>The second claim these make is about absence.</b> A pivot the series cannot supply a period for — no
/// prior session in the window, a window that starts after that session opened, a session the series covers
/// with a single bar, no ATR at the bar the period closed on — is <i>missing</i>. Not zero, not the nearest
/// period, and not a period assembled from the part of it the window happens to hold.
/// </para>
/// </remarks>
public sealed class PivotLevelMethodTests
{
    private static DateOnly Aug16 => new(2026, 8, 16); // Sunday
    private static DateOnly Aug17 => new(2026, 8, 17); // Monday
    private static DateOnly Aug18 => new(2026, 8, 18); // Tuesday

    /// <summary>The shipped 16:00 Central close, no declared holidays — so the session reopens at 17:00.</summary>
    private static BarSessionCalendar Calendar => new(new TimeOnly(16, 0), []);

    /// <summary>Bar <paramref name="hour"/> o'clock Central on <paramref name="date"/>, as UTC.</summary>
    private static DateTimeOffset At(DateOnly date, int hour) =>
        MarketClock.FromMarket(date, new TimeOnly(hour, 0)).ToUniversalTime();

    private static Bar Hour(DateOnly date, int hour, decimal open, decimal high, decimal low, decimal close) =>
        new(At(date, hour), open, high, low, close, Volume: 1_000, ContractId: "CON.F.US.EP.U26");

    /// <summary>The production defaults, which is what makes the numbers below the numbers the tool serves.</summary>
    /// <remarks>
    /// <c>Lookback</c>, <c>RightLookback</c> and <c>Source</c> describe how a <i>pivot</i> is found by
    /// dominance and are never read by this family — a session's high is its high. They are nevertheless
    /// <b>validated</b>, which <c>session</c> does not do, and
    /// <see cref="AnOptionSetNoStageCouldHonour_IsRefusedUpFront_IncludingFieldsThisFamilyNeverReads"/> pins
    /// why: this family hands its zones to <see cref="KeyLevels.ApplyWidthCap"/> and
    /// <see cref="KeyLevels.ApplyLevelCap"/>, and those own the whole option set.
    /// </remarks>
    private static KeyLevelOptions Options => new();

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE WORKED FIXTURE — hourly bars, one contract, two sessions. Times are CENTRAL.
    //
    //  The calendar closes at 16:00 and reopens at 17:00, so a trade date's session runs from 17:00 the
    //  previous evening to 16:00 on the date itself.
    //
    //    #   bar opens        open  high   low  close   trade date       which session
    //    0   Sun 16th 17:00    100   106    96    104   Mon 17th   ─┐
    //    1   Mon 17th 09:00    104   120    99    112   Mon 17th    │  the PRIOR PERIOD
    //    2   Mon 17th 15:00    112   114   108    111   Mon 17th   ─┘
    //    3   Mon 17th 17:00    111   118   110    116   Tue 18th   ─┐  the CURRENT session
    //    4   Tue 18th 09:00    116   124   113    122   Tue 18th   ─┘  the last bar — close 122
    //
    //  READ THE PRIOR PERIOD OFF THE TABLE (bars 0-2, the whole of Monday's session):
    //
    //    O = 100   bar 0's OPEN — the first price the session traded at
    //    H = 120   bar 1
    //    L =  96   bar 0
    //    C = 111   bar 2's CLOSE — the last price the session traded at
    //    range H - L = 24
    //
    //  ATR is 2 at every bar. The scale is the ATR at bar 2, the bar the period closed on, so
    //
    //    significance = range / ATR   = 24 / 2   = 12    -- ONE number for every line in the family
    //    half-band    = ATR * 0.5 / 2 = 2 * 0.5 / 2 = 0.5 -- every zone is its price +/- 0.5
    //
    //  THE FIVE FORMULAS, worked from O=100 H=120 L=96 C=111:
    //
    //    classic     P  = (H + L + C) / 3 = (120 + 96 + 111) / 3 = 327 / 3 = 109
    //                R1 = 2P - L      = 218 - 96  = 122     S1 = 2P - H      = 218 - 120 =  98
    //                R2 = P + (H - L) = 109 + 24  = 133     S2 = P - (H - L) = 109 - 24  =  85
    //                R3 = H + 2(P - L) = 120 + 26 = 146     S3 = L - 2(H - P) = 96 - 22  =  74
    //
    //    fibonacci   P  = (H + L + C) / 3 = 109, each leg a fraction of the 24-point range
    //                R1 = P + 0.382*24 = 109 +  9.168 = 118.168   S1 = 109 -  9.168 =  99.832
    //                R2 = P + 0.618*24 = 109 + 14.832 = 123.832   S2 = 109 - 14.832 =  94.168
    //                R3 = P + 1.000*24 = 109 + 24     = 133       S3 = 109 - 24     =  85
    //
    //    camarilla   measured from the CLOSE rather than from a pivot; range * 1.1 = 26.4
    //                R1 = C + 26.4/12 = 111 +  2.2 = 113.2    S1 = 111 -  2.2 = 108.8
    //                R2 = C + 26.4/6  = 111 +  4.4 = 115.4    S2 = 111 -  4.4 = 106.6
    //                R3 = C + 26.4/4  = 111 +  6.6 = 117.6    S3 = 111 -  6.6 = 104.4
    //                R4 = C + 26.4/2  = 111 + 13.2 = 124.2    S4 = 111 - 13.2 =  97.8
    //
    //    woodie      P  = (H + L + 2C) / 4 = (120 + 96 + 222) / 4 = 438 / 4 = 109.5
    //                R1 = 2P - L      = 219 - 96 = 123        S1 = 2P - H      = 219 - 120 = 99
    //                R2 = P + (H - L) = 133.5                 S2 = P - (H - L) = 85.5
    //
    //    demark      C (111) is ABOVE O (100), so X = 2H + L + C = 240 + 96 + 111 = 447
    //                P  = X / 4     = 111.75
    //                R1 = X / 2 - L = 223.5 -  96 = 127.5
    //                S1 = X / 2 - H = 223.5 - 120 = 103.5
    //
    //  THEN THE SHARED PIPELINE, exactly as `swing` reaches it: merge overlaps (`R-3.1`), drop a zone wider
    //  than 2.5% of its own midpoint (`R-3.9`), relabel against the last close (`R-3.3`), cap the count at 12
    //  (`R-3.9`). At a half-band of 0.5 no two lines in any of the five sets are within one point of each
    //  other, so nothing merges; every zone is 1 point wide on a midpoint of at least 74, which is 1.35% at
    //  worst; and no set has more than eight lines. So the only stage that changes anything here is the
    //  relabelling, and it changes a great deal:
    //
    //    THE LAST CLOSE IS 122. Every zone whose top is under it is support today whichever side it was
    //    computed as -- so camarilla's R1, R2 and R3 and fibonacci's R1 all come back as SUPPORT. Only
    //    classic's R1 zone [121.5, 122.5] contains 122, and there the formation's own reading stands: R1 is
    //    named a resistance by the formula, and that is the seed `ApplyClose` leaves alone.
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> Fixture =>
    [
        Hour(Aug16, 17, 100, 106, 96, 104),
        Hour(Aug17, 9, 104, 120, 99, 112),
        Hour(Aug17, 15, 112, 114, 108, 111),
        Hour(Aug17, 17, 111, 118, 110, 116),
        Hour(Aug18, 9, 116, 124, 113, 122),
    ];

    /// <summary>An ATR of 2 at every bar.</summary>
    private static IReadOnlyList<decimal?> FlatAtr(int count) => [.. Enumerable.Repeat((decimal?)2m, count)];

    private static IReadOnlyList<KeyLevelZone> Detect(
        PivotFormula formula,
        IReadOnlyList<Bar>? bars = null,
        IReadOnlyList<decimal?>? atr = null,
        KeyLevelOptions? options = null)
    {
        IReadOnlyList<Bar> series = bars ?? Fixture;
        return new PivotLevelMethod(formula, Calendar)
            .Detect(series, atr ?? FlatAtr(series.Count), options ?? Options);
    }

    private static IEnumerable<(decimal Bottom, decimal Top, KeyLevelKind Kind, int TouchCount, decimal Significance)>
        Shape(IReadOnlyList<KeyLevelZone> zones) =>
        zones.Select(z => (z.Bottom, z.Top, z.Kind, z.TouchCount, z.Significance));

    /// <summary>Every formula the domain can compute.</summary>
    /// <remarks>
    /// <b>Read off the enum rather than listed</b>, so a sixth formula joins every sweep below by being
    /// written. A hardcoded list of the five is the shape gh#259 names and rejects: the variant that escapes
    /// it does so silently, and here that would mean a formula nothing ever checked produced a level at all.
    /// <see cref="EachFormulaCarriesItsOwnStableLowercaseName"/> is the one case that must be edited by hand
    /// when a sixth arrives, which is deliberate — a name is a request vocabulary, not a derived string.
    /// </remarks>
    private static PivotFormula[] AllFormulas =>
        [.. Enum.GetValues<PivotFormula>().Where(f => f != PivotFormula.Unknown)];

    // ── The five formulas, each against its own hand-worked arithmetic ────────────────────────────────

    [Fact]
    public void Classic_ProducesTheSevenLevelsDerivedFromThePeriod()
    {
        // 74, 85, 98, 109, 122, 133, 146 -- each +/- 0.5, each at significance 12, none of them merging.
        Shape(Detect(PivotFormula.Classic)).Should().Equal(
            (73.5m, 74.5m, KeyLevelKind.Support, 1, 12m),      // S3 = L - 2(H - P) = 96 - 22
            (84.5m, 85.5m, KeyLevelKind.Support, 1, 12m),      // S2 = P - (H - L) = 109 - 24
            (97.5m, 98.5m, KeyLevelKind.Support, 1, 12m),      // S1 = 2P - H = 218 - 120
            (108.5m, 109.5m, KeyLevelKind.Support, 1, 12m),    // P  = 327 / 3
            (121.5m, 122.5m, KeyLevelKind.Resistance, 1, 12m), // R1 = 2P - L = 218 - 96; the close sits inside it
            (132.5m, 133.5m, KeyLevelKind.Resistance, 1, 12m), // R2 = P + (H - L)
            (145.5m, 146.5m, KeyLevelKind.Resistance, 1, 12m)); // R3 = H + 2(P - L)
    }

    [Fact]
    public void Fibonacci_ProducesTheSevenLevelsDerivedFromThePeriod()
    {
        // The legs are 0.382, 0.618 and 1.000 of the 24-point range: 9.168, 14.832 and 24 exactly.
        Shape(Detect(PivotFormula.Fibonacci)).Should().Equal(
            (84.5m, 85.5m, KeyLevelKind.Support, 1, 12m),       // S3 = 109 - 24
            (93.668m, 94.668m, KeyLevelKind.Support, 1, 12m),   // S2 = 109 - 14.832
            (99.332m, 100.332m, KeyLevelKind.Support, 1, 12m),  // S1 = 109 - 9.168
            (108.5m, 109.5m, KeyLevelKind.Support, 1, 12m),     // P  = 109
            (117.668m, 118.668m, KeyLevelKind.Support, 1, 12m), // R1 = 109 + 9.168 -- under 122, so SUPPORT today
            (123.332m, 124.332m, KeyLevelKind.Resistance, 1, 12m), // R2 = 109 + 14.832
            (132.5m, 133.5m, KeyLevelKind.Resistance, 1, 12m));    // R3 = 109 + 24
    }

    [Fact]
    public void Camarilla_ProducesTheEightLevelsDerivedFromThePriorClose()
    {
        // Eight lines and no central pivot: the published camarilla set is measured from the prior CLOSE,
        // and adding (H + L + C) / 3 to it would report another method's line under this one's name.
        Shape(Detect(PivotFormula.Camarilla)).Should().Equal(
            (97.3m, 98.3m, KeyLevelKind.Support, 1, 12m),     // S4 = 111 - 13.2
            (103.9m, 104.9m, KeyLevelKind.Support, 1, 12m),   // S3 = 111 -  6.6
            (106.1m, 107.1m, KeyLevelKind.Support, 1, 12m),   // S2 = 111 -  4.4
            (108.3m, 109.3m, KeyLevelKind.Support, 1, 12m),   // S1 = 111 -  2.2
            (112.7m, 113.7m, KeyLevelKind.Support, 1, 12m),   // R1 = 111 +  2.2 -- under 122, so SUPPORT today
            (114.9m, 115.9m, KeyLevelKind.Support, 1, 12m),   // R2 = 111 +  4.4 -- likewise
            (117.1m, 118.1m, KeyLevelKind.Support, 1, 12m),   // R3 = 111 +  6.6 -- likewise
            (123.7m, 124.7m, KeyLevelKind.Resistance, 1, 12m)); // R4 = 111 + 13.2
    }

    [Fact]
    public void Woodie_ProducesTheFiveLevelsDerivedFromItsDoubleWeightedClose()
    {
        // Woodie weights the close twice: P = (H + L + 2C) / 4 = 438 / 4 = 109.5, half a point above
        // classic's 109. Everything hangs off that, so R1 and S1 are half a point higher than classic's too.
        Shape(Detect(PivotFormula.Woodie)).Should().Equal(
            (85m, 86m, KeyLevelKind.Support, 1, 12m),          // S2 = 109.5 - 24
            (98.5m, 99.5m, KeyLevelKind.Support, 1, 12m),      // S1 = 219 - 120
            (109m, 110m, KeyLevelKind.Support, 1, 12m),        // P  = 438 / 4
            (122.5m, 123.5m, KeyLevelKind.Resistance, 1, 12m), // R1 = 219 - 96
            (133m, 134m, KeyLevelKind.Resistance, 1, 12m));    // R2 = 109.5 + 24
    }

    // ── DeMark's three branches ───────────────────────────────────────────────────────────────────────
    //
    //  DeMark is the one formula that reads the period's OPEN, and it branches on where the close finished
    //  against it. The three fixtures below differ in exactly one field -- bar 0's open -- so the period's
    //  H, L and C are 120, 96 and 111 in all three and only the branch moves.

    [Fact]
    public void DeMark_TakesTheUpBranch_WhenThePeriodClosedAboveItsOpen()
    {
        // C (111) > O (100): X = 2H + L + C = 240 + 96 + 111 = 447.
        //   P = 447/4 = 111.75    R1 = 223.5 - 96 = 127.5    S1 = 223.5 - 120 = 103.5
        Shape(Detect(PivotFormula.DeMark)).Should().Equal(
            (103m, 104m, KeyLevelKind.Support, 1, 12m),
            (111.25m, 112.25m, KeyLevelKind.Support, 1, 12m),
            (127m, 128m, KeyLevelKind.Resistance, 1, 12m));
    }

    [Fact]
    public void DeMark_TakesTheDownBranch_WhenThePeriodClosedBelowItsOpen()
    {
        // Bar 0 opens at 120 instead of 100, so the session opened at its own high and closed 9 points under
        // it. C (111) < O (120): X = H + 2L + C = 120 + 192 + 111 = 423.
        //   P = 423/4 = 105.75    R1 = 211.5 - 96 = 115.5    S1 = 211.5 - 120 = 91.5
        IReadOnlyList<Bar> openedHigh = WithFirstBar(Hour(Aug16, 17, 120, 120, 96, 104));

        Shape(Detect(PivotFormula.DeMark, openedHigh)).Should().Equal(
            (91m, 92m, KeyLevelKind.Support, 1, 12m),
            (105.25m, 106.25m, KeyLevelKind.Support, 1, 12m),
            (115m, 116m, KeyLevelKind.Support, 1, 12m));
    }

    [Fact]
    public void DeMark_TakesTheEqualBranch_WhenThePeriodClosedExactlyAtItsOpen()
    {
        // Bar 0 opens at 111, which is where the session closes. C == O: X = H + L + 2C = 120 + 96 + 222 = 438.
        //   P = 438/4 = 109.5    R1 = 219 - 96 = 123    S1 = 219 - 120 = 99
        //
        // The equality branch is the one an implementation drops without noticing, because a series in which
        // a session closes exactly where it opened is rare and neither inequality branch fails loudly on it:
        // taking the up branch here would answer 111.75 / 127.5 / 103.5 and taking the down branch 105.75 /
        // 115.5 / 91.5. Both are well-formed pivot sets and neither is this one.
        IReadOnlyList<Bar> openedFlat = WithFirstBar(Hour(Aug16, 17, 111, 111, 96, 104));

        Shape(Detect(PivotFormula.DeMark, openedFlat)).Should().Equal(
            (98.5m, 99.5m, KeyLevelKind.Support, 1, 12m),
            (109m, 110m, KeyLevelKind.Support, 1, 12m),
            (122.5m, 123.5m, KeyLevelKind.Resistance, 1, 12m));
    }

    private static IReadOnlyList<Bar> WithFirstBar(Bar replacement) => [replacement, .. Fixture.Skip(1)];

    // ── Each of the five actually produces something ──────────────────────────────────────────────────

    [Fact]
    public void EveryFormula_ProducesLevelsFromTheWorkedFixture()
    {
        // PR #252's review finding 2: `NotThrow` passes for `return [];`. Each formula is separately pinned
        // against hand-derived numbers above; this is the same claim swept, so a sixth formula added to the
        // enum and to the catalogue cannot arrive silent.
        foreach (PivotFormula formula in AllFormulas)
        {
            Detect(formula).Should().NotBeEmpty(
                formula + " found nothing in a series carrying a complete prior session");
        }
    }

    // ── Absence: what the family does when it cannot compute ──────────────────────────────────────────

    [Fact]
    public void APriorPeriodTheWindowDoesNotReachTheOpeningOf_IsAbsentRatherThanTakenFromThePartItHolds()
    {
        // Drop bar 0 -- Sunday evening, which is the OPENING of Monday's session. Bars 1 and 2 still carry
        // Monday's trade date, so a method that took "the extremes of whatever prior-session bars are
        // loaded" would read H=120, L=99, C=111 and answer with a classic pivot of (120 + 99 + 111) / 3 =
        // 110. Every one of those three prices is real; 99 is not that session's low, and 110 is not its
        // pivot. The rule is `session`'s, unchanged: a period is reported only when the series reaches the
        // opening of it.
        IReadOnlyList<Bar> truncated = [.. Fixture.Skip(1)];

        foreach (PivotFormula formula in AllFormulas)
        {
            Detect(formula, truncated).Should().BeEmpty(formula + " answered from a partial prior session");
        }
    }

    [Fact]
    public void APriorPeriodTheSeriesCoversWithOneBar_IsAbsentRatherThanReadOffABarThatMaySpanMore()
    {
        // The same session, at a resolution that cannot resolve it. Bar 0 here carries Monday's whole
        // session in one bar and its open, high, low and close are 100, 120, 96 and 111 -- the very numbers
        // the worked fixture derives seven classic levels from. They are not reported, and the asymmetry
        // that would be the defect is the point: this family refuses a period the window only partly covers
        // (above), so it must also refuse one the series covers too coarsely to distinguish. At a daily
        // resolution and above, one bar carries a trade date and everything else it spans, and `Bar` records
        // no width to tell the two apart -- so "Monday's high" would be the high of whatever that bar
        // covered. Two bars are the least that shows the series samples the session more finely than the
        // session itself.
        IReadOnlyList<Bar> daily =
        [
            Hour(Aug16, 17, 100, 120, 96, 111),
            Hour(Aug17, 17, 111, 118, 110, 116),
            Hour(Aug18, 9, 116, 124, 113, 122),
        ];

        foreach (PivotFormula formula in AllFormulas)
        {
            Detect(formula, daily).Should().BeEmpty(
                formula + " read a prior session off a single bar that may span more than one");
        }
    }

    [Fact]
    public void ASeriesHoldingOnlyTheCurrentSession_IsAbsentRatherThanPivotingOffItself()
    {
        // Bars 3 and 4 only. There is no session before this one in the window, and the current session is
        // still trading -- pivoting off a period that has not finished is a level that repaints (`R-3.4`).
        IReadOnlyList<Bar> currentOnly = [.. Fixture.Skip(3)];

        foreach (PivotFormula formula in AllFormulas)
        {
            Detect(formula, currentOnly).Should().BeEmpty(formula + " produced levels with no prior session");
        }
    }

    [Fact]
    public void ADayThatDidNotTrade_IsNotAPriorPeriod()
    {
        // Monday is declared shut, so every bar carrying Monday's trade date now sits outside a session and
        // there is no prior period at all -- rather than the family quietly stepping back to some earlier
        // day the window does not hold. Counting back one calendar day, or 1,440 minutes, would have
        // answered from a session that did not happen.
        BarSessionCalendar shut = new(new TimeOnly(16, 0), [Aug17]);

        foreach (PivotFormula formula in AllFormulas)
        {
            new PivotLevelMethod(formula, shut)
                .Detect(Fixture, FlatAtr(Fixture.Count), Options)
                .Should().BeEmpty(formula + " pivoted off a day the calendar says did not trade");
        }
    }

    [Fact]
    public void NoAtrAtTheBarThePeriodClosedOn_LeavesTheWholeFamilyAbsent()
    {
        // A zone is sized and scored in ATR multiples, and the scale is the ATR at the bar the period closed
        // on -- bar 2. With none there, every line in the family loses its width and its significance at
        // once, because they all share one scale. The honest answer is nothing: not a zero-width line at
        // each price, and not a width borrowed from a neighbouring bar.
        decimal?[] atr = [.. FlatAtr(Fixture.Count)];
        atr[2] = null;

        foreach (PivotFormula formula in AllFormulas)
        {
            Detect(formula, atr: atr).Should().BeEmpty(formula + " scaled a zone with an ATR no bar supplied");
        }
    }

    [Fact]
    public void APeriodThatMovedLessThanTheSignificanceFloor_IsAbsent()
    {
        // Significance for this family is the PERIOD's own range in ATR multiples -- 24 / 2 = 12 -- which is
        // one number shared by every line, because no computed line has neighbours to stand clear of. So the
        // floor applies to the family as a whole: above it every line is reported, above 12 none is. Pinned
        // at both sides of the boundary, because a floor that only ever passes is a floor nobody has watched
        // bite.
        Detect(PivotFormula.Classic, options: Options with { MinSignificance = 12m }).Should().HaveCount(7);
        Detect(PivotFormula.Classic, options: Options with { MinSignificance = 12.5m }).Should().BeEmpty();
    }

    // ── The two caps this family honours ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheWidthCapDropsTheCheapestLevels_BecauseAFixedWidthIsALargerShareOfASmallerPrice()
    {
        // Every zone here is exactly one point wide, so what varies is the price it sits on: one point is
        // 1.35% of 74 and 0.68% of 146. The cap is a percentage of the zone's own midpoint (`R-3.9`), so at
        // 1% it keeps everything at 100 or above and drops S1, S2 and S3.
        //
        //   S3  74 -> 1/74  = 1.351%  dropped      P  109 -> 0.917%  kept
        //   S2  85 -> 1/85  = 1.176%  dropped      R1 122 -> 0.820%  kept
        //   S1  98 -> 1/98  = 1.020%  dropped      R2 133 -> 0.752%  kept
        //                                          R3 146 -> 0.685%  kept
        IReadOnlyList<KeyLevelZone> capped =
            Detect(PivotFormula.Classic, options: Options with { MaxZoneWidthPercent = 1m });

        capped.Select(z => z.Midpoint).Should().Equal(109m, 122m, 133m, 146m);
    }

    [Fact]
    public void TheLevelCapFallsThroughToPrice_BecauseOnePeriodGivesEveryLineTheSameSignificance()
    {
        // The cap ranks by significance, then touches, then price (gh#245). Every line in a pivot family
        // comes from one period, so significance is 12 for all eight and touches are 1 for all eight: the
        // ranking cannot separate them on strength and settles on the next key in its total order, which is
        // the bottom edge. Three survivors are therefore camarilla's three lowest, S4, S3 and S2.
        //
        // That is a real consequence of giving a whole family one significance rather than an accident, and
        // it is pinned rather than left to be discovered: a caller capping this family is choosing the
        // cheapest levels, not the strongest ones.
        IReadOnlyList<KeyLevelZone> capped =
            Detect(PivotFormula.Camarilla, options: Options with { MaxLevels = 3 });

        Shape(capped).Should().Equal(
            (97.3m, 98.3m, KeyLevelKind.Support, 1, 12m),
            (103.9m, 104.9m, KeyLevelKind.Support, 1, 12m),
            (106.1m, 107.1m, KeyLevelKind.Support, 1, 12m));
    }

    [Fact]
    public void AFormulaThatLeavesThePriceDomain_NeverReachesTheAnswer()
    {
        // A pivot formula is unbounded arithmetic on three prices, and on a session whose range is most of
        // its own low the far legs run off the bottom of the price scale. Here the period is
        // O=100 H=200 L=99 C=100, so P = 399/3 = 133 and
        //
        //    S3 = L - 2(H - P) = 99 - 134 = -35     a price no instrument can trade at
        //    S2 = P - (H - L)  = 133 - 101 = 32     a real price, but a 1-point zone is 3.1% of it
        //
        // Neither is reported, and both are stopped by the same rule rather than by a special case: the
        // width cap compares `width * 100 <= MaxZoneWidthPercent * midpoint`, and at the shipped 2.5% that
        // needs a midpoint of at least 40. A midpoint of zero or below can never satisfy it, because a
        // zone's width is always positive. Measured here rather than argued: this fixture is the reason the
        // claim is in the file at all.
        IReadOnlyList<Bar> wide =
        [
            Hour(Aug16, 17, 100, 200, 99, 150),
            Hour(Aug17, 15, 150, 160, 100, 100),
            Hour(Aug17, 17, 100, 105, 95, 100),
            Hour(Aug18, 9, 100, 105, 95, 100),
        ];

        IReadOnlyList<KeyLevelZone> zones = Detect(PivotFormula.Classic, wide);

        // significance = range / ATR = 101 / 2 = 50.5, and the last close is 100.
        Shape(zones).Should().Equal(
            (65.5m, 66.5m, KeyLevelKind.Support, 1, 50.5m),      // S1 = 266 - 200
            (132.5m, 133.5m, KeyLevelKind.Resistance, 1, 50.5m), // P  = 399 / 3
            (166.5m, 167.5m, KeyLevelKind.Resistance, 1, 50.5m), // R1 = 266 -  99
            (233.5m, 234.5m, KeyLevelKind.Resistance, 1, 50.5m), // R2 = 133 + 101
            (267.5m, 268.5m, KeyLevelKind.Resistance, 1, 50.5m)); // R3 = 200 + 68

        zones.Should().OnlyContain(z => z.Bottom > 0m, "a price at or below zero is not a level");
    }

    // ── The seam this family sits on ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOptionSetNoStageCouldHonour_IsRefusedUpFront_IncludingFieldsThisFamilyNeverReads()
    {
        // `Lookback` describes how a pivot is found by dominance and nothing in this family consults it --
        // yet a zero is refused, and the refusal is deliberate rather than incidental. This family honours
        // `MaxZoneWidthPercent` and `MaxLevels` by handing its zones to KeyLevels.ApplyWidthCap and
        // ApplyLevelCap, and those own the whole option set: refusing here, before the arithmetic runs,
        // is what stops the same refusal arriving from the middle of the pipeline on some calls and not on
        // others. `session`, which applies neither cap, accepts these fields instead -- that difference is
        // real, and gh#259 is where it is settled.
        Action zeroLookback = () => Detect(PivotFormula.Classic, options: Options with { Lookback = 0 });

        zeroLookback.Should().Throw<ArgumentOutOfRangeException>();

        // And it fires whether or not the window happens to hold a period to compute over, which is the
        // property that makes it useful: a refusal that waits for data is one the operator meets late.
        Action noPeriodEither = () => Detect(
            PivotFormula.Classic, [.. Fixture.Skip(3)], options: Options with { Lookback = 0 });

        noPeriodEither.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AZoneWidthOfZero_IsRefused_BecauseALineNeedsAWidthBeforeItIsAZone()
    {
        Action noWidth = () => Detect(PivotFormula.Classic, options: Options with { ZoneAtrMultiple = 0m });

        noWidth.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AnUnsetFormula_IsRefused_RatherThanPickingOneByAccident()
    {
        // The same rule `PivotSource.Unknown` follows: a zero default here would answer with somebody's
        // pivot set and never say whose.
        Action unset = () => Detect(PivotFormula.Unknown);

        unset.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MisalignedAtr_IsRefused()
    {
        Action shortAtr = () => Detect(PivotFormula.Classic, atr: FlatAtr(Fixture.Count - 1));

        shortAtr.Should().Throw<ArgumentException>().WithMessage("*align*");
    }

    // ── Names and family ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachFormulaCarriesItsOwnStableLowercaseName()
    {
        AllFormulas.Select(f => new PivotLevelMethod(f, Calendar).Name).Should().Equal(
            "pivot-classic", "pivot-fibonacci", "pivot-camarilla", "pivot-woodie", "pivot-demark");
    }

    [Fact]
    public void AllFiveDeclareOneFamily_SoAScoreCanDiscountThemTogether()
    {
        // The whole reason the family identifier exists (gh#232, gh#259): five methods agreeing is not five
        // confirmations, it is one prior open/high/low/close transformed five ways. A score that counted
        // them separately would read 5/5 exactly where a reader most wants to trust it.
        AllFormulas.Select(f => new PivotLevelMethod(f, Calendar).Family).Should().Equal(
            "pivot", "pivot", "pivot", "pivot", "pivot");
    }

    [Fact]
    public void TheFiveAgreeOnTheirPeriod_WhichIsWhyTheyShareABudget()
    {
        // The correlation, made checkable rather than asserted in prose: move ONE of the period's three
        // prices and every one of the five answers differently. The close is the price to move, because it
        // leaves the range at 24 and therefore the significance at 12 -- so what shifts is the arithmetic
        // itself and not the score attached to it. Bar 2 closes at 108 instead of 111, and
        //
        //   classic P -> 324/3 = 108   fibonacci P -> 108   camarilla is measured from the close outright
        //   woodie  P -> 432/4 = 108   demark X -> 2H + L + C = 444, P -> 111
        //
        // Five methods that all move when one number does are not five confirmations of anything.
        IReadOnlyList<Bar> closedLower =
            [.. Fixture.Take(2), Hour(Aug17, 15, 112, 114, 108, 108), .. Fixture.Skip(3)];

        foreach (PivotFormula formula in AllFormulas)
        {
            Detect(formula, closedLower).Select(z => z.Midpoint).Should().NotBeEquivalentTo(
                Detect(formula).Select(z => z.Midpoint),
                formula + " ignored a change to the period every one of the five is computed from");
        }
    }
}
