using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// The <c>session</c> method — prior day, prior week, the overnight range and the initial balance.
/// </summary>
/// <remarks>
/// <para>
/// Every number below is <b>hand-derived from the definition</b>, in the shape gh#242 established for
/// <c>swing</c>: the fixture is written out as a table, the extremes are read off it by eye, and the zone
/// arithmetic is worked through in the comment beside the assertion. Nothing here round-trips the
/// implementation. gh#232's trap 5 is that <c>KeyLevels</c> had no numeric baseline for two years and nobody
/// noticed; a structural assertion on a new method would recreate it.
/// </para>
/// <para>
/// <b>The second claim these make is about absence.</b> A session level that cannot be computed — no prior
/// week inside the window, an overnight leg the series stops in the middle of, an initial balance still
/// forming — is <i>missing</i>, and the tests below pin it missing rather than zero, rather than the nearest
/// available substitute, and rather than a level built from the part of the period the window happens to
/// hold. A prior-day high taken from half a prior day is exactly gh#213's oldest failure class: a
/// well-formed, believable number that no trade produced as that day's high.
/// </para>
/// </remarks>
public sealed class SessionLevelMethodTests
{
    private static DateOnly Aug13 => new(2026, 8, 13); // Thursday
    private static DateOnly Aug14 => new(2026, 8, 14); // Friday
    private static DateOnly Aug16 => new(2026, 8, 16); // Sunday
    private static DateOnly Aug17 => new(2026, 8, 17); // Monday
    private static DateOnly Aug18 => new(2026, 8, 18); // Tuesday

    /// <summary>
    /// A 16:00 Central close with the four days before Friday 14 August declared holidays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The holidays are <b>load-bearing, not colour</b>. A prior week is only reported when the window
    /// reaches that week's <i>first trading session</i>, and without them the week beginning Monday
    /// 10 August opens on Sunday the 9th — which the fixture below does not reach, so every prior-week
    /// assertion would be asserting an absence for the wrong reason. Declaring the 10th to the 13th shut
    /// makes Friday the 14th that week's first trading day, and its session opens on Thursday evening,
    /// which is exactly where the fixture starts.
    /// </para>
    /// <para>
    /// They also carry the card's own requirement that <b>a prior "day" that was not a trading day is not a
    /// prior day</b> — pinned directly in
    /// <see cref="AHolidayIsNotAPriorDay_SoThePriorDayIsTheLastDayThatActuallyTraded"/>, which shuts Monday
    /// and watches the prior day move back to Friday.
    /// </para>
    /// </remarks>
    private static BarSessionCalendar Calendar => new(
        new TimeOnly(16, 0),
        [new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 12), Aug13]);

    /// <summary>Bar <paramref name="hour"/> o'clock Central on <paramref name="date"/>, as UTC.</summary>
    private static DateTimeOffset At(DateOnly date, int hour) =>
        MarketClock.FromMarket(date, new TimeOnly(hour, 0)).ToUniversalTime();

    private static Bar Hour(DateOnly date, int hour, decimal open, decimal high, decimal low, decimal close) =>
        new(At(date, hour), open, high, low, close, Volume: 1_000, ContractId: "CON.F.US.EP.U26");

    /// <summary>The production defaults, which is what makes the numbers below the numbers the tool serves.</summary>
    /// <remarks>
    /// <c>Lookback</c> and <c>Source</c> are left at their defaults and are <b>never read</b> by this method:
    /// a session high is the session's high, not a pivot measured from a chosen price series. That they are
    /// ignored rather than validated is deliberate and stated on <c>SessionLevels.Compute</c>.
    /// </remarks>
    private static KeyLevelOptions Options => new(
        Lookback: 5,
        Source: PivotSource.HeikinAshiBody,
        ZoneAtrMultiple: 0.5m,
        MinSignificance: 0.5m);

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    //  THE WORKED FIXTURE — hourly bars, one contract, three sessions. Times are CENTRAL.
    //
    //  The calendar closes at 16:00 and reopens at 17:00, so a trade date's session runs from 17:00 the
    //  previous evening to 16:00 on the date itself.
    //
    //    #   bar opens        open  high   low  close   trade date        which session
    //    0   Thu 13th 17:00    100   104    99    102   Fri 14th   ─┐
    //    1   Fri 14th 09:00    102   108   101    106   Fri 14th    │  the PRIOR WEEK
    //    2   Fri 14th 15:00    106   107   103    105   Fri 14th   ─┘
    //    3   Sun 16th 17:00    105   110   104    109   Mon 17th   ─┐
    //    4   Mon 17th 09:00    109   120   108    118   Mon 17th    │  the PRIOR DAY
    //    5   Mon 17th 15:00    118   119   112    115   Mon 17th   ─┘
    //    6   Mon 17th 17:00    115   130   114    128   Tue 18th   ─┐  overnight + initial balance
    //    7   Mon 17th 20:00    128   131   125    126   Tue 18th    │  overnight
    //    8   Mon 17th 23:00    126   127   121    122   Tue 18th   ─┘  overnight
    //    9   Tue 18th 02:00    122   124   118    123   Tue 18th      (past midnight: not overnight)
    //   10   Tue 18th 09:00    123   126   117    125   Tue 18th      the last bar — close 125
    //
    //  ATR is 2 at every bar, so every zone is the same width and every significance divides by 2.
    //
    //  READ OFF THE TABLE:
    //
    //    prior week (bars 0-2)   high 108 (bar 1)   low  99 (bar 0)   close 105 (bar 2)   range  9
    //    prior day  (bars 3-5)   high 120 (bar 4)   low 104 (bar 3)   close 115 (bar 5)   range 16
    //    overnight  (bars 6-8)   high 131 (bar 7)   low 114 (bar 6)                       range 17
    //    initial balance (bar 6) high 130           low 114                               range 16
    //
    //  The initial balance is the first hour of the session — 17:00 to 18:00 — which is bar 6 alone.
    //  The overnight leg is the part of Tuesday's session that traded on MONDAY's calendar date, so it is
    //  bars 6-8 and stops at midnight; bar 9 is Tuesday's own morning and is not overnight.
    //
    //  EACH LINE BECOMES A ZONE. Half-band = atr * ZoneAtrMultiple / 2 = 2 * 0.5 / 2 = 0.5, the same
    //  arithmetic KeyLevels.ZoneFor does for a pivot — so the line-to-zone tolerance is `ZoneAtrMultiple`,
    //  which the tool already reports as `detection.zoneAtrMultiple`. Significance is the period's own range
    //  in ATR multiples.
    //
    //    prior week   high 108 -> [107.5, 108.5]  sig  9/2 = 4.5   formed bar 1
    //                 low   99 -> [ 98.5,  99.5]  sig  4.5         formed bar 0
    //                 close 105 -> [104.5, 105.5] sig  4.5         formed bar 2
    //    prior day    high 120 -> [119.5, 120.5]  sig 16/2 = 8     formed bar 4
    //                 low  104 -> [103.5, 104.5]  sig  8           formed bar 3
    //                 close 115 -> [114.5, 115.5] sig  8           formed bar 5
    //    overnight    high 131 -> [130.5, 131.5]  sig 17/2 = 8.5   formed bar 7
    //                 low  114 -> [113.5, 114.5]  sig  8.5         formed bar 6
    //    initial bal. high 130 -> [129.5, 130.5]  sig 16/2 = 8     formed bar 6
    //                 low  114 -> [113.5, 114.5]  sig  8           formed bar 6
    //
    //  THEN MERGE, per kind. Highs seed resistance, lows seed support, and a close seeds from where it sits
    //  against the last close (115 and 105 are both under 125, so both seed support).
    //
    //    support, by bottom:  [98.5,99.5]  [103.5,104.5]  [104.5,105.5]  [113.5,114.5]x2  [114.5,115.5]
    //      98.5..99.5   stands alone                                  -> [ 98.5,  99.5] touch 1 sig 4.5
    //      103.5..104.5 touches 104.5..105.5 at 104.5                 -> [103.5, 105.5] touch 2 sig 8
    //                   earliest of bar 3 (Sun 17:00) and bar 2 (Fri 15:00) is bar 2
    //      113.5..114.5 twice, then 114.5..115.5 touches at 114.5     -> [113.5, 115.5] touch 3 sig 8.5
    //                   earliest of bar 6, bar 6 and bar 5 is bar 5 (Mon 15:00)
    //    resistance, by bottom:  [107.5,108.5]  [119.5,120.5]  [129.5,130.5]  [130.5,131.5]
    //      107.5..108.5 stands alone                                  -> [107.5, 108.5] touch 1 sig 4.5
    //      119.5..120.5 stands alone                                  -> [119.5, 120.5] touch 1 sig 8
    //      129.5..130.5 touches 130.5..131.5 at 130.5                 -> [129.5, 131.5] touch 2 sig 8.5
    //                   earliest of bar 6 (Mon 17:00) and bar 7 (Mon 20:00) is bar 6
    //
    //  THEN LABEL AGAINST THE LAST CLOSE, 125 (`R-3.3`). Everything with a top under 125 is support today,
    //  whichever side it formed on — so the prior week's HIGH of 108 and the prior day's HIGH of 120 both
    //  come back as support, which is the whole point of relabelling.
    //
    //    [ 98.5,  99.5] support     [103.5, 105.5] support     [107.5, 108.5] support
    //    [113.5, 115.5] support     [119.5, 120.5] support     [129.5, 131.5] resistance
    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static IReadOnlyList<Bar> Fixture =>
    [
        Hour(Aug13, 17, 100, 104, 99, 102),
        Hour(Aug14, 9, 102, 108, 101, 106),
        Hour(Aug14, 15, 106, 107, 103, 105),
        Hour(Aug16, 17, 105, 110, 104, 109),
        Hour(Aug17, 9, 109, 120, 108, 118),
        Hour(Aug17, 15, 118, 119, 112, 115),
        Hour(Aug17, 17, 115, 130, 114, 128),
        Hour(Aug17, 20, 128, 131, 125, 126),
        Hour(Aug17, 23, 126, 127, 121, 122),
        Hour(Aug18, 2, 122, 124, 118, 123),
        Hour(Aug18, 9, 123, 126, 117, 125),
    ];

    /// <summary>An ATR of 2 at every bar.</summary>
    private static IReadOnlyList<decimal?> FlatAtr(int count) => [.. Enumerable.Repeat((decimal?)2m, count)];

    private static IReadOnlyList<KeyLevelZone> Detect(
        IReadOnlyList<Bar> bars, IReadOnlyList<decimal?>? atr = null) =>
        new SessionLevelMethod(Calendar).Detect(bars, atr ?? FlatAtr(bars.Count), Options);

    [Fact]
    public void TheFixtureProducesTheSixZonesDerivedAbove()
    {
        IReadOnlyList<KeyLevelZone> zones = Detect(Fixture);

        zones.Should().HaveCount(6);

        zones.Select(z => (z.Bottom, z.Top, z.Kind, z.TouchCount, z.Significance)).Should().Equal(
            (98.5m, 99.5m, KeyLevelKind.Support, 1, 4.5m),
            (103.5m, 105.5m, KeyLevelKind.Support, 2, 8m),
            (107.5m, 108.5m, KeyLevelKind.Support, 1, 4.5m),
            (113.5m, 115.5m, KeyLevelKind.Support, 3, 8.5m),
            (119.5m, 120.5m, KeyLevelKind.Support, 1, 8m),
            (129.5m, 131.5m, KeyLevelKind.Resistance, 2, 8.5m));

        // A merge keeps the EARLIEST formation, so a level dates from when it was first respected. Read off
        // the table: bar 0, bar 2, bar 1, bar 5, bar 4, bar 6.
        zones.Select(z => z.FormedAtBucket).Should().Equal(
            At(Aug13, 17), At(Aug14, 15), At(Aug14, 9), At(Aug17, 15), At(Aug17, 9), At(Aug17, 17));
    }

    [Fact]
    public void APriorWeekTheWindowDoesNotReachTheOpeningOf_IsAbsentRatherThanTakenFromThePartItHolds()
    {
        // Drop bar 0 — Thursday evening, which is the opening leg of Friday's session and therefore the
        // opening of that whole trading week. Bars 1 and 2 still carry Friday's trade date, so a method that
        // simply took "the extremes of whatever prior-week bars are loaded" would answer with a week high of
        // 108 and a week low of 101. Both are real prices; neither is that week's high or low.
        IReadOnlyList<Bar> truncated = [.. Fixture.Skip(1)];

        IReadOnlyList<KeyLevelZone> zones = Detect(truncated);

        zones.Select(z => (z.Bottom, z.Top, z.Kind, z.TouchCount, z.Significance)).Should().Equal(
            // The prior DAY survives: Monday's session opens on Sunday evening, which is still inside the
            // window. Only the week is gone.
            (103.5m, 104.5m, KeyLevelKind.Support, 1, 8m),
            (113.5m, 115.5m, KeyLevelKind.Support, 3, 8.5m),
            (119.5m, 120.5m, KeyLevelKind.Support, 1, 8m),
            (129.5m, 131.5m, KeyLevelKind.Resistance, 2, 8.5m));

        zones.Should().NotContain(
            z => z.Contains(101m),
            "101 is the low of the prior-week bars this window happens to hold, and it is not that week's low");
    }

    [Fact]
    public void AHolidayIsNotAPriorDay_SoThePriorDayIsTheLastDayThatActuallyTraded()
    {
        // Monday the 17th is shut. Every bar whose trade date is Monday now sits outside a session, so the
        // day before Tuesday that actually traded is FRIDAY — and the prior-day levels must be Friday's
        // 108/99/105, not Monday's 120/104/115. Counting back one calendar day, or 1,440 minutes, would
        // report a high of 120 from a session that did not happen.
        BarSessionCalendar shut = new(new TimeOnly(16, 0), [new DateOnly(2026, 8, 10), Aug13, Aug17]);

        IReadOnlyList<KeyLevelZone> zones =
            new SessionLevelMethod(shut).Detect(Fixture, FlatAtr(Fixture.Count), Options);

        zones.Should().NotContain(
            z => z.Contains(120m), "120 is Monday's high, and Monday did not trade");
        zones.Should().NotContain(
            z => z.Contains(115m), "115 is Monday's close, and Monday did not trade");
        zones.Should().Contain(
            z => z.Contains(108m), "Friday is the prior day once Monday is shut, and 108 is Friday's high");

        // Monday's EVENING still reopens — that leg belongs to Tuesday, which trades — so the overnight and
        // the initial balance are untouched by the holiday.
        zones.Should().Contain(z => z.Contains(131m), "the overnight high is Tuesday's session, not Monday's");
        zones.Should().Contain(z => z.Contains(130m), "the initial balance is Tuesday's session, not Monday's");
    }

    [Fact]
    public void AnOvernightLegTheSeriesStopsInsideOf_IsAbsentWhileTheInitialBalanceIsNot()
    {
        // Bars 0-8: the series ends at 23:00 Monday, still inside Tuesday's overnight leg. The range so far
        // is 131/114 and it is still moving, so reporting it would be a level that repaints on the next bar
        // (`R-3.4`). The initial balance, by contrast, closed at 18:00 and is final.
        IReadOnlyList<Bar> stillOvernight = [.. Fixture.Take(9)];

        IReadOnlyList<KeyLevelZone> zones = Detect(stillOvernight);

        // Last close is now bar 8's, 122. Prior week and prior day are unchanged; the overnight pair is gone
        // and the initial balance's two lines stand on their own:
        //   IB high 130 -> [129.5, 130.5] sig 8, resistance (129.5 > 122)
        //   IB low  114 -> [113.5, 114.5] sig 8 — which now merges only with the prior day's close at
        //                  [114.5, 115.5] sig 8, touching at 114.5 -> [113.5, 115.5] touch 2, formed bar 5.
        zones.Select(z => (z.Bottom, z.Top, z.Kind, z.TouchCount, z.Significance)).Should().Equal(
            (98.5m, 99.5m, KeyLevelKind.Support, 1, 4.5m),
            (103.5m, 105.5m, KeyLevelKind.Support, 2, 8m),
            (107.5m, 108.5m, KeyLevelKind.Support, 1, 4.5m),
            (113.5m, 115.5m, KeyLevelKind.Support, 2, 8m),
            (119.5m, 120.5m, KeyLevelKind.Support, 1, 8m),
            (129.5m, 130.5m, KeyLevelKind.Resistance, 1, 8m));

        zones.Should().NotContain(
            z => z.Contains(131m), "131 is the overnight high so far, and the overnight leg has not closed");
    }

    [Fact]
    public void ASessionThatHasOnlyJustOpened_ProducesNothingRatherThanAnInitialBalanceStillForming()
    {
        // One bar: Tuesday's session has been open for an hour and nothing before it is loaded. There is no
        // prior day, no prior week, no closed overnight leg, and the initial balance is the bar itself and
        // has not finished. An empty answer is the honest one.
        IReadOnlyList<Bar> justOpened = [Fixture[6]];

        Detect(justOpened).Should().BeEmpty();
    }

    [Fact]
    public void AWindowStartingAtTheCurrentSessionsOpen_HasOnlyItsOwnOvernightAndInitialBalance()
    {
        // Bars 6-10. The window begins exactly at Tuesday's reopen, so nothing before Tuesday is reachable
        // and the prior day and prior week are both absent. What survives is what this session itself
        // produced, and there is no substitute for the rest.
        IReadOnlyList<Bar> currentSessionOnly = [.. Fixture.Skip(6)];

        Detect(currentSessionOnly).Select(z => (z.Bottom, z.Top, z.Kind, z.TouchCount, z.Significance))
            .Should().Equal(
                // ON low 114 (sig 8.5) and IB low 114 (sig 8) are the same price from the same bar.
                (113.5m, 114.5m, KeyLevelKind.Support, 2, 8.5m),
                (129.5m, 131.5m, KeyLevelKind.Resistance, 2, 8.5m));
    }

    [Fact]
    public void ACalendarWhoseReopenLandsAfterMidnight_ProducesNothingRatherThanABoundaryItDisowns()
    {
        // Every period here is anchored to the instant its session opened, and that instant is looked for at
        // the calendar's reopen time on the PREVIOUS evening. A 23:30 close with the default one-hour window
        // reopens at 00:30, which is not an evening — it is the small hours of the trade date itself, so the
        // previous evening is the wrong place to look and the boundary found there is not this session's.
        //
        // Measured: with close 23:30 the candidate instant is 2026-08-17 00:30 Central, and the calendar
        // reads it as trade date 08-17, not the 08-18 it was asked about, because 00:30 is before that day's
        // 23:30 close. So the candidate is refused and every period goes absent. With the shipped 16:00
        // close the same lookup for 08-18 lands on 2026-08-17 17:00 and the calendar agrees.
        //
        // The point is that the boundary is not RECONSTRUCTED from the close and the window — it is handed
        // back to the calendar and kept only if the calendar agrees. A rule that assumed "the evening before"
        // would have answered here with a full set of levels measured from the wrong day.
        BarSessionCalendar afterMidnightReopen = new(new TimeOnly(23, 30), []);

        new SessionLevelMethod(afterMidnightReopen).Detect(Fixture, FlatAtr(Fixture.Count), Options)
            .Should().BeEmpty();
    }

    [Fact]
    public void AMissingAtrAtTheBarThatFormedALevel_DropsThatLevelAndLeavesTheOthersAlone()
    {
        // A zone is sized and scored in ATR multiples, so with no ATR at the bar that made the level there
        // is no scale to size it with. Bar 4 is the prior day's high, so [119.5, 120.5] must simply not be
        // there — not a zero-width line at 120, and not one scaled by a borrowed ATR from a neighbour.
        decimal?[] atr = [.. FlatAtr(Fixture.Count)];
        atr[4] = null;

        IReadOnlyList<KeyLevelZone> zones = Detect(Fixture, atr);

        zones.Should().NotContain(z => z.Contains(120m));
        zones.Select(z => (z.Bottom, z.Top)).Should().Equal(
            (98.5m, 99.5m), (103.5m, 105.5m), (107.5m, 108.5m), (113.5m, 115.5m), (129.5m, 131.5m));
    }
}
