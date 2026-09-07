using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// Named sessions on the trade date <see cref="BarSessionCalendar"/> already models, and the UTC windows they
/// resolve to.
/// </summary>
/// <remarks>
/// Every number here is hand-derived from Central wall-clock and checked against the 2026 daylight-saving
/// calendar (CDT, UTC-5, until 2026-11-01 02:00; CST, UTC-6, after). A window derived by adding a fixed offset
/// is right for half the year and silently wrong for the other half, which is why the daylight case is pinned
/// on both sides of the same weekend.
/// </remarks>
public sealed class SessionWindowsTests
{
    private static BarSessionCalendar Calendar(params string[] holidays) =>
        BarSessionCalendar.Parse("16:00", holidays);

    /// <summary>An absolute instant, stated in UTC — which is the only form these windows are handed out in.</summary>
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static SessionDefinition Shipped(string name) =>
        SessionDefinition.Defaults.Single(definition => definition.Name == name);

    [Fact]
    public void Validate_AcceptsTheFourShippedDefaults()
    {
        BarSessionCalendar calendar = Calendar();

        SessionDefinition.Defaults.Select(definition => definition.Name)
            .Should().Equal("full", "rth", "asia", "europe");

        foreach (SessionDefinition definition in SessionDefinition.Defaults)
        {
            Action act = () => SessionWindows.Validate(definition, calendar);
            act.Should().NotThrow("'" + definition.Name + "' is a shipped default");
        }
    }

    [Fact]
    public void Validate_RefusesASessionThatSpansTheMaintenanceWindow()
    {
        // 15:00 is 22h into the session, 17:30 is half an hour into the NEXT one. Expressed as offsets from
        // the open the pair simply runs backwards, which is the same rule that lets `asia` (0h -> 9h) cross
        // midnight without a special case.
        Action act = () => SessionWindows.Validate(
            new SessionDefinition("bad", new TimeOnly(15, 0), new TimeOnly(17, 30), 30), Calendar());

        // "*session*" would pass against every other rule Validate enforces -- the name, the base and the
        // grid all say "session" too. The offsets from the open are what identifies THIS rule: 15:00 is 22h
        // into the session and 17:30 is half an hour into the next one, so the pair runs backwards.
        act.Should().Throw<ArgumentException>()
            .WithMessage("*run forwards*", "the rule broken is the one about direction")
            .WithMessage("*22:00:00 to 00:30:00*", "and the message shows the pair as offsets from the open");
    }

    [Fact]
    public void Validate_RefusesABaseThatDoesNotDivideTheHour()
    {
        // Central is a whole-hour UTC offset, so a wall-clock boundary on a grid that divides the hour lands
        // on the UTC grid under both CST and CDT. 120 does not: 17:00 Central is 23:00Z in winter, which is
        // off a 120-minute UTC grid.
        Action act = () => SessionWindows.Validate(
            Shipped("full") with { BaseResolutionMinutes = 120 }, Calendar());

        act.Should().Throw<ArgumentException>().WithMessage("*60*");
    }

    [Fact]
    public void Validate_RefusesABaseAboveSixty()
    {
        Action act = () => SessionWindows.Validate(
            Shipped("full") with { BaseResolutionMinutes = 90 }, Calendar());

        act.Should().Throw<ArgumentException>().WithMessage("*60*");
    }

    [Fact]
    public void Validate_RefusesABoundaryOffTheBaseGrid()
    {
        // 08:45 is 15h45m into the session; on a 30-minute base that boundary is not a bucket start, so the
        // first bar of the session would be a partial nobody stored.
        Action act = () => SessionWindows.Validate(
            new SessionDefinition("offgrid", new TimeOnly(8, 45), new TimeOnly(15, 0), 30), Calendar());

        act.Should().Throw<ArgumentException>().WithMessage("*grid*");
    }

    [Fact]
    public void Validate_RefusesABoundaryOffTheWallClockGrid_WhenTheSessionOpenIsNotOnIt()
    {
        // `BarGapDetector.AlignUp` anchors buckets on a fixed UTC-midnight grid, so the property that has to
        // hold is that each boundary's WALL-CLOCK instant lands on that grid -- not that its offset from the
        // session open does. The two agree only when the open is itself on the grid, which the shipped 16:00
        // close makes true and an operator's 13:20 close does not.
        //
        // A 13:20 close reopens at 14:20, so 14:20 -> 13:20 is 0h -> 23h measured from the open: on the base
        // grid in that coordinate and off it in UTC. Its window is [19:20Z, 18:20Z), whose expected buckets
        // begin at 20:00Z, so the session's first forty minutes would sit outside a bar claiming the
        // session's own bounds.
        Action offTheHour = () => SessionWindows.Validate(
            new SessionDefinition("day", new TimeOnly(14, 20), new TimeOnly(13, 20), 60),
            BarSessionCalendar.Parse("13:20", []));

        offTheHour.Should().Throw<ArgumentException>()
            .WithMessage("*grid*", "the rule broken is the one about the stored UTC bucket grid")
            .WithMessage("*14:20*", "and the message names the boundary that breaks it");

        // The same failure at a 30-minute base: a 16:15 close reopens at 17:15, so 08:45 is 15h30m into the
        // session -- on the 30-minute grid measured from the open, and a quarter-hour off it in UTC.
        Action offTheHalfHour = () => SessionWindows.Validate(
            new SessionDefinition("x", new TimeOnly(8, 45), new TimeOnly(15, 15), 30),
            BarSessionCalendar.Parse("16:15", []));

        offTheHalfHour.Should().Throw<ArgumentException>()
            .WithMessage("*grid*").WithMessage("*08:45*");
    }

    [Fact]
    public void Validate_AcceptsAWallClockAlignedDefinition_OnAnOddCalendar()
    {
        // The rule is about the grid, not about the calendar. The same 13:20 close that refuses
        // 14:20 -> 13:20 admits 15:00 -> 13:00, because both of those boundaries are on the hour and so land
        // on the UTC bucket grid under either offset. Measured from the 14:20 open the pair is 40m -> 22h40m,
        // which the offset-coordinate rule this replaced would have refused.
        Action act = () => SessionWindows.Validate(
            new SessionDefinition("day", new TimeOnly(15, 0), new TimeOnly(13, 0), 60),
            BarSessionCalendar.Parse("13:20", []));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_RefusesAnEmptyOrUppercaseName()
    {
        // The name is a storage key, like IIndicator.Name: renaming or case-shifting it orphans every row
        // already written under the old one.
        BarSessionCalendar calendar = Calendar();

        Action empty = () => SessionWindows.Validate(
            new SessionDefinition("", new TimeOnly(8, 30), new TimeOnly(15, 0), 30), calendar);
        empty.Should().Throw<ArgumentException>().WithMessage("*name*");

        Action upper = () => SessionWindows.Validate(
            new SessionDefinition("RTH", new TimeOnly(8, 30), new TimeOnly(15, 0), 30), calendar);
        upper.Should().Throw<ArgumentException>().WithMessage("*name*");
    }

    [Fact]
    public void Validate_RefusesAnEndAtOrBeforeTheStart()
    {
        Action act = () => SessionWindows.Validate(
            new SessionDefinition("empty", new TimeOnly(10, 0), new TimeOnly(10, 0), 30), Calendar());

        // Same rule as the maintenance-window case, reached by the degenerate route: 10:00 is 17h into the
        // session, so both boundaries land on the same instant and the window is empty rather than backwards.
        act.Should().Throw<ArgumentException>()
            .WithMessage("*run forwards*", "the rule broken is the one about direction")
            .WithMessage("*17:00:00 to 17:00:00*", "and both offsets are the same instant");
    }

    [Fact]
    public void WindowFor_RthOnATuesday_Is0830To1500Central()
    {
        // Tuesday 2026-08-18 is CDT (UTC-5): 08:30 -> 13:30Z, 15:00 -> 20:00Z. Both boundaries sit on the
        // trade date itself, because both are before the 17:00 reopen.
        BarRange? window = SessionWindows.WindowFor(Calendar(), Shipped("rth"), new DateOnly(2026, 8, 18));

        window.Should().Be(new BarRange(Utc(2026, 8, 18, 13, 30), Utc(2026, 8, 18, 20, 0)));
    }

    [Fact]
    public void WindowFor_AsiaOnAMonday_OpensSundayEvening()
    {
        // The off-by-one-evening rule: Monday's session opens Sunday 17:00 Central (22:00Z under CDT) and
        // `asia` closes at 02:00 on the trade date itself (07:00Z).
        BarRange? window = SessionWindows.WindowFor(Calendar(), Shipped("asia"), new DateOnly(2026, 8, 17));

        window.Should().Be(new BarRange(Utc(2026, 8, 16, 22, 0), Utc(2026, 8, 17, 7, 0)));
    }

    [Fact]
    public void WindowFor_Full_ShiftsUtcBoundsAcrossADaylightChange()
    {
        // US DST ends 2026-11-01 02:00. Both sessions are 17:00 -> 16:00 CENTRAL; their UTC bounds differ by
        // an hour, which is exactly what a fixed-offset derivation would get wrong.
        BarSessionCalendar calendar = Calendar();
        SessionDefinition full = Shipped("full");

        SessionWindows.WindowFor(calendar, full, new DateOnly(2026, 10, 30))
            .Should().Be(new BarRange(Utc(2026, 10, 29, 22, 0), Utc(2026, 10, 30, 21, 0)));

        SessionWindows.WindowFor(calendar, full, new DateOnly(2026, 11, 2))
            .Should().Be(new BarRange(Utc(2026, 11, 1, 23, 0), Utc(2026, 11, 2, 22, 0)));
    }

    [Fact]
    public void WindowFor_IsNull_OnASaturday()
    {
        SessionWindows.WindowFor(Calendar(), Shipped("full"), new DateOnly(2026, 8, 22)).Should().BeNull();
    }

    [Fact]
    public void WindowFor_IsNull_OnADeclaredHoliday()
    {
        SessionWindows.WindowFor(Calendar("2026-08-19"), Shipped("full"), new DateOnly(2026, 8, 19))
            .Should().BeNull();
    }

    [Fact]
    public void TradeDatesIn_SkipsAHoliday_AndTheEveningBeforeIt()
    {
        BarSessionCalendar calendar = Calendar("2026-08-19");
        BarRange week = new(Utc(2026, 8, 17, 0, 0), Utc(2026, 8, 22, 0, 0));

        // Monday the 17th is excluded because its session OPENED Sunday at 22:00Z, before the window starts,
        // so the window does not hold the whole thing. Wednesday the 19th is a declared holiday: that session
        // does not exist at all, and neither does the Tuesday evening leg that would have opened it.
        SessionWindows.TradeDatesIn(calendar, Shipped("full"), week)
            .Should().Equal(new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 21));

        // `asia` closes at 02:00 rather than 16:00, but it opens on the same evening leg, so the same two
        // dates drop out for the same two reasons.
        SessionWindows.TradeDatesIn(calendar, Shipped("asia"), week)
            .Should().Equal(new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 21));

        SessionWindows.WindowFor(calendar, Shipped("asia"), new DateOnly(2026, 8, 19)).Should().BeNull();
    }

    [Fact]
    public void TradeDatesIn_OnlyIncludesASessionWhollyInsideTheWindow()
    {
        // The window opens at 10:00 Central on Tuesday, an hour and a half after Tuesday's RTH open, so
        // Tuesday is not wholly inside it. Wednesday is.
        BarRange window = new(Utc(2026, 8, 18, 15, 0), Utc(2026, 8, 20, 0, 0));

        SessionWindows.TradeDatesIn(Calendar(), Shipped("rth"), window)
            .Should().Equal(new DateOnly(2026, 8, 19));
    }

    [Fact]
    public void LastClosedTradeDates_AnchorsOnTheLastClosedSession_NotTheCurrentOne()
    {
        BarSessionCalendar calendar = Calendar();
        DateTimeOffset midTuesday = Utc(2026, 8, 18, 17, 0); // 12:00 Central, mid-session.

        // Tuesday's RTH closes at 20:00Z, after `now`, so the last two CLOSED sessions are Monday and the
        // Friday before it -- the weekend carries no trade date.
        SessionWindows.LastClosedTradeDates(calendar, Shipped("rth"), midTuesday, 2)
            .Should().Equal(new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 17));

        // `full` closes at 21:00Z, also after `now`, so the same pair.
        SessionWindows.LastClosedTradeDates(calendar, Shipped("full"), midTuesday, 2)
            .Should().Equal(new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 17));

        // At exactly the close the session IS closed, so Tuesday joins.
        SessionWindows.LastClosedTradeDates(calendar, Shipped("full"), Utc(2026, 8, 18, 21, 0), 2)
            .Should().Equal(new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 18));
    }

    [Fact]
    public void LastClosedTradeDates_ReturnsTheSingleLastClosedSession_WhenCountIsOne()
    {
        // One is the case every "the last session" caller asks for, and the smallest walk the bound admits.
        // Tuesday's RTH runs 13:30Z to 20:00Z and `now` is inside it, so the answer is Monday.
        SessionWindows.LastClosedTradeDates(Calendar(), Shipped("rth"), Utc(2026, 8, 18, 17, 0), 1)
            .Should().Equal(new DateOnly(2026, 8, 17));
    }

    [Fact]
    public void LastClosedTradeDates_Refuses_WhenTheCalendarRunsOut()
    {
        // Every day the bounded walk can reach is a declared holiday, so there is no closed session to
        // return. Refusing lets the caller say so rather than hand back a shorter list that reads as complete.
        string[] holidays =
        [
            .. Enumerable.Range(0, 30)
                .Select(offset => new DateOnly(2026, 7, 25).AddDays(offset)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        ];

        Action act = () => SessionWindows.LastClosedTradeDates(
            Calendar(holidays), Shipped("rth"), Utc(2026, 8, 18, 17, 0), 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LastClosedWalkSpanDays_IsFourPerSessionPlusFifteen()
    {
        // The bound the walk stops at, EXPOSED rather than restated. It used to be a local inside
        // LastClosedTradeDates, so every caller that needed to talk about it -- the tool guard's refusal text
        // and two test fixtures -- wrote the arithmetic out again, and three copies of a number is three
        // places to go stale when one of them changes (gh#500).
        //
        // Four days per session plus fifteen: five trading days a week is 1.4 calendar days per session, so
        // four is generous room for holidays, and the fifteen covers a long closure near the anchor.
        SessionWindows.LastClosedWalkSpanDays(1).Should().Be(19);
        SessionWindows.LastClosedWalkSpanDays(100).Should().Be(415);
        SessionWindows.LastClosedWalkSpanDays(5_000).Should().Be(20_015);
    }

    [Fact]
    public void LastClosedTradeDates_RefusesANonPositiveCount()
    {
        Action act = () => SessionWindows.LastClosedTradeDates(
            Calendar(), Shipped("rth"), Utc(2026, 8, 18, 17, 0), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
