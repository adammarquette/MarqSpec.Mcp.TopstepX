using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What a session-bar read refuses, and what it hands back when it does not (gh#500).
/// </summary>
/// <remarks>
/// <para>
/// A session window is not a bar window. The caps still bind — the row cap on how many trade dates one answer
/// may carry, the detection cap on the base buckets underneath them — but they bind on different quantities,
/// so <c>ValidateWindow</c> cannot be reused: it would measure rows in session-length buckets, and
/// <c>ValidateResolution</c> refuses anything a session long before it got there.
/// </para>
/// <para>
/// <b>No store and no container.</b> Every case here refuses, or resolves a date list from the calendar
/// alone; neither reaches a connection, which is what keeps these in the unit tier (gh#387). The fixture is
/// two objects — the guards and a calendar — because that is all the guards take.
/// </para>
/// </remarks>
public sealed class SessionGuardTests
{
    /// <summary>The shipped `rth` session: 08:30–15:00 Central, derived from 30-minute base bars.</summary>
    private static SessionDefinition Rth =>
        SessionDefinition.Defaults.Single(static d => d.Name == "rth");

    /// <summary>A calendar with no declared holidays, so every weekday is a trade date.</summary>
    private static BarSessionCalendar Calendar => BarSessionCalendar.Parse("16:00", []);

    [Fact]
    public void ValidateSessionWindow_RefusesAnEmptyWindow()
    {
        // The same fault ValidateWindow names first, and it has to be named here too: TradeDatesIn answers an
        // empty window with an empty list, which reads as "no session traded" rather than "you asked for
        // nothing".
        DateTimeOffset instant = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        Action refuse = () => Guards().ValidateSessionWindow(instant, instant, Rth, Calendar);

        refuse.Should().Throw<McpException>().WithMessage("*empty or inverted*");
    }

    [Fact]
    public void ValidateSessionWindow_RefusesAnEndPastTheCalendarHorizon()
    {
        // The calendar walk starts by asking which market date the end falls on and stepping a day past it,
        // so an end at the top of the DateOnly range faults inside the Domain (gh#110's shape, one tool
        // along) rather than refusing on this boundary.
        DateTimeOffset from = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        Action refuse = () =>
            Guards().ValidateSessionWindow(from, DateTimeOffset.MaxValue, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("toUtc", "the refusal names the parameter the caller can change")
            .And.Contain(
                "session calendar can reason about", "and says which bound it is past");
    }

    [Fact]
    public void ValidateSessionWindow_RefusesAWindowOverTheBaseBucketCap_NamingTheWindow()
    {
        // The detection cap binds in BASE buckets -- the 30-minute bars `rth` is aggregated from -- because
        // those are what a read enumerates. One bucket over, at a row count (about 3,700 trade dates) that is
        // comfortably inside MaxRows: the two caps are on different quantities and neither implies the other.
        DateTimeOffset from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddTicks(
            (BarGapDetector.MaxBucketsPerPass + 1L) * Rth.BaseResolutionMinutes * TimeSpan.TicksPerMinute);

        Action refuse = () => Guards().ValidateSessionWindow(from, to, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("That window", "the refusal names what the caller can narrow")
            .And.Contain("250001", "and the real bucket count, not the cap alone")
            .And.Contain("gap-detection pass", "which is the bound it is over");
    }

    [Fact]
    public void ValidateSessionWindow_RefusesMoreTradeDatesThanMaxRows_NamingTheRealCount()
    {
        // Two weeks of August 2026 hold ten weekday `rth` sessions -- Mon 3rd to Fri 14th, the 17th's session
        // closing after the window ends. Hand-counted, so the message is checked against the calendar rather
        // than against the walk that produced it.
        DateTimeOffset from = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

        Action refuse = () => Guards(maxRows: 3).ValidateSessionWindow(from, to, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain(
                "names 10 rth trade dates",
                "the caller is told how many rows the window really asks for, not merely that it is too many")
            .And.Contain("cap of 3", "and the cap it is over")
            .And.Contain(
                "refused rather than truncated",
                "because a shortened series is indistinguishable from a complete one");
    }

    [Fact]
    public void ValidateSessionWindow_ListsOnlyWhollyContainedSessions()
    {
        // Opens half an hour after Monday's `rth` open and ends exactly on Wednesday's close. Monday is
        // clipped, so it is left out rather than served short: a session bar built from part of a session is
        // a wrong number wearing an ordinary face. Wednesday's close lands on the end, and the window is
        // half-open, so it is in.
        DateTimeOffset from = new(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 8, 5, 20, 0, 0, TimeSpan.Zero);

        SessionWindowPlan plan = Guards().ValidateSessionWindow(from, to, Rth, Calendar);

        plan.TradeDates.Should().Equal(
            [new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 5)],
            "a session only partly inside the window is excluded, never truncated");
        plan.Window.Should().Be(new BarRange(from, to), "the plan reports the window it validated");
    }

    [Fact]
    public void ValidateSessionCount_TranslatesAnUnsatisfiableCount_IntoARefusalNamingCount()
    {
        // MaxRows admits 5,000 and the bounded walk covers (5,000 * 4) + 15 = 20,015 calendar days, so a
        // calendar that trades every weekday satisfies this count comfortably -- the two bounds only disagree
        // when the calendar is sparse. This one trades Fridays alone, which is what a holiday-dense calendar
        // does to the walk: about 2,860 closed sessions in the span, and the Domain throws a raw
        // ArgumentOutOfRangeException that must not reach a caller.
        DateTimeOffset now = MarketClock.FromMarket(new DateOnly(2026, 8, 6), new TimeOnly(20, 0));

        Action refuse = () => Guards().ValidateSessionCount(5_000, Rth, FridaysOnly(now), now);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("count 5000", "the refusal names the parameter and the value")
            .And.Contain("Ask for fewer", "and says what to do about it");
    }

    [Fact]
    public void ValidateSessionCount_ReturnsTheLastClosedDates_Ascending()
    {
        // Thursday evening, five hours after `rth` closed at 15:00 Central. Thursday's session IS one of the
        // three -- it has closed -- and Friday's, which has not opened, is not. Oldest first, so a caller can
        // read the list as a series without reversing it.
        DateTimeOffset now = MarketClock.FromMarket(new DateOnly(2026, 8, 6), new TimeOnly(20, 0));

        IReadOnlyList<DateOnly> dates = Guards().ValidateSessionCount(3, Rth, Calendar, now);

        dates.Should().Equal(
            [new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 6)],
            "the anchor is the last CLOSED session, never the one in progress");
    }

    private static ToolGuards Guards(int maxRows = 5_000) =>
        new(Options.Create(new MarketDataOptions { MaxRows = maxRows }));

    /// <summary>
    /// A calendar whose only trading day is Friday, for the whole span the count walk can reach back over.
    /// </summary>
    /// <param name="now">The instant the walk starts from.</param>
    /// <returns>The calendar.</returns>
    /// <remarks>
    /// Declared as holidays rather than modelled some other way because that is the only lever a calendar
    /// has, and it is the real shape of the bug: a venue closed most of a stretch carries fewer closed
    /// sessions than the walk's day span suggests.
    /// </remarks>
    private static BarSessionCalendar FridaysOnly(DateTimeOffset now)
    {
        DateOnly cursor = MarketClock.MarketDate(now).AddDays(1);
        List<DateOnly> holidays = [];

        for (int i = 0; i < 20_100; i++)
        {
            DateOnly day = cursor.AddDays(-i);
            if (day.DayOfWeek is not DayOfWeek.Friday)
            {
                holidays.Add(day);
            }
        }

        return new BarSessionCalendar(new TimeOnly(16, 0), holidays);
    }
}
