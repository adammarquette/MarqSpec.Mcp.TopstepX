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
    /// <summary>A count inside <c>MaxRows</c> that a sparse calendar still cannot satisfy.</summary>
    private const int UnsatisfiableCount = 5_000;

    /// <summary>
    /// The span the sparse calendar declares holidays over — the walk's, plus a margin at both ends.
    /// </summary>
    /// <remarks>
    /// <b>Read off <see cref="SessionWindows.LastClosedWalkSpanDays"/> rather than restated.</b> The span used
    /// to be a local inside <c>LastClosedTradeDates</c>, so this fixture wrote <c>(count * 4) + 15</c> out
    /// again with a comment admitting it was a copy; the accessor exists now, and one number in one place
    /// cannot drift from itself. The 85 is margin: it covers the cursor's own day (the walk starts one day
    /// <i>ahead</i> of <c>now</c>'s market date) and leaves room for the span to grow a little before this
    /// fixture stops covering it.
    /// </remarks>
    private static int SparseCalendarDays =>
        SessionWindows.LastClosedWalkSpanDays(UnsatisfiableCount) + 85;

    /// <summary>The shipped `rth` session: 08:30–15:00 Central, derived from 30-minute base bars.</summary>
    private static SessionDefinition Rth =>
        SessionDefinition.Defaults.Single(static d => d.Name == "rth");

    /// <summary>A calendar with no declared holidays, so every weekday is a trade date.</summary>
    private static BarSessionCalendar Calendar => BarSessionCalendar.Parse("16:00", []);

    /// <summary>The start of a window one base bucket wider than a single gap-detection pass.</summary>
    private static DateTimeOffset OverTheBucketCapFrom => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Its end — <c>MaxBucketsPerPass + 1</c> buckets at the `rth` base of 30 minutes.</summary>
    private static DateTimeOffset OverTheBucketCapTo =>
        OverTheBucketCapFrom.AddTicks(
            (BarGapDetector.MaxBucketsPerPass + 1L) * Rth.BaseResolutionMinutes * TimeSpan.TicksPerMinute);

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
        Action refuse = () =>
            Guards().ValidateSessionWindow(OverTheBucketCapFrom, OverTheBucketCapTo, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("That window", "the refusal names what the caller can narrow")
            .And.Contain("250001", "and the real bucket count, not the cap alone")
            .And.Contain("gap-detection pass", "which is the bound it is over");
    }

    [Fact]
    public void ValidateSessionWindow_RefusesTheBucketCap_NamingOnlyRemediesThisToolHas()
    {
        // The shared refusal offers three ways out, and two of them are get_bars's: a session-bar caller has
        // no bar count and no resolution argument -- the base resolution is the operator's, on the session
        // definition. Advice a caller cannot act on reads as a dead end, and sends them looking for a
        // parameter that is not there.
        Action refuse = () => Guards().ValidateSessionWindow(OverTheBucketCapFrom, OverTheBucketCapTo, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("Narrow the window", "which is the one lever this caller does have")
            .And.NotContain("ask for fewer bars", "there is no bar count on this tool")
            .And.NotContain(
                "use a coarser resolution",
                "and no resolution argument either -- the base resolution belongs to the session definition, "
                + "so the remedy points at the operator rather than at the caller");
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
    public void ValidateSessionWindow_RefusesTheBucketCapBeforeTheRowCap()
    {
        // BOTH caps are breached here -- MaxRows of 3 against about 3,700 trade dates, and one base bucket
        // over the detection cap -- so the ORDER is what decides which refusal the caller gets, and the order
        // is the opposite of ValidateWindow's. It has to be: the row count is not arithmetic on this surface,
        // it is a calendar walk over every day the window touches (5,208 of them here), and the bucket span
        // is what bounds that walk. Measuring rows first would mean doing the unbounded work to find out that
        // the work was unbounded.
        Action refuse = () => Guards(maxRows: 3)
            .ValidateSessionWindow(OverTheBucketCapFrom, OverTheBucketCapTo, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("gap-detection pass", "the base-bucket cap is the one measured first")
            .And.NotContain(
                "trade dates",
                "the row cap is not reached at all -- reaching it means running the very walk the bucket "
                + "span was checked to bound");
    }

    [Fact]
    public void ValidateSessionWindow_ListsOnlyWhollyContainedSessions()
    {
        // Opens half an hour after Monday's `rth` open and ends exactly on Wednesday's close. Monday is
        // clipped, so it is left out rather than served short: a session bar built from part of a session is
        // a wrong number wearing an ordinary face. Wednesday's close lands on the end, and the window is
        // half-open, so it is in.
        //
        // Both instants carry a NON-ZERO offset -- 09:00-05:00 is 14:00Z, 15:00-05:00 is 20:00Z -- because
        // the plan's window is stored UTC, and DateTimeOffset equality compares instants: a plan that forgot
        // to normalise would still compare equal, and only the offset says it happened.
        DateTimeOffset from = new(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(-5));
        DateTimeOffset to = new(2026, 8, 5, 15, 0, 0, TimeSpan.FromHours(-5));

        SessionWindowPlan plan = Guards().ValidateSessionWindow(from, to, Rth, Calendar);

        plan.TradeDates.Should().Equal(
            [new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 5)],
            "a session only partly inside the window is excluded, never truncated");
        plan.Window.Should().Be(
            new BarRange(from.ToUniversalTime(), to.ToUniversalTime()),
            "the plan reports the window it validated");
        plan.Window.Start.Offset.Should().Be(
            TimeSpan.Zero, "the store and the wire are UTC, so the plan normalises rather than passing on an offset");
        plan.Window.End.Offset.Should().Be(TimeSpan.Zero, "and the same at the other end");
    }

    [Fact]
    public void ValidateSessionWindow_RefusesAWindowThatClipsEverySession_NamingTheNearestWhole()
    {
        // The exact scenario from gh#568: nine hours of a Monday over an `rth` session that runs 13:30Z to
        // 20:00Z (08:30-15:00 Central, CDT in August). The window overlaps the session heavily and still
        // clips its last two hours, so TradeDatesIn admits nothing -- and left unrefused this answers exactly
        // like the EMPTY window ValidateSessionWindow_RefusesAnEmptyWindow already refuses to avoid: bars: []
        // and absent: [] both, reading as "ES did not trade" rather than "the window is narrower than any
        // session".
        DateTimeOffset from = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 8, 3, 18, 0, 0, TimeSpan.Zero);

        Action refuse = () => Guards().ValidateSessionWindow(from, to, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain(
                "2026-08-03T09:00:00.0000000+00:00 .. 2026-08-03T18:00:00.0000000+00:00",
                "the refusal names the window that was asked for")
            .And.Contain("no whole rth session", "and the session it names none of")
            .And.Contain(
                "nearest whole rth session is 2026-08-03",
                "and the trade date the nearest whole session sits on")
            .And.Contain(
                "2026-08-03T13:30:00.0000000+00:00 to 2026-08-03T20:00:00.0000000+00:00",
                "with its bounds in UTC, so the caller can widen to it")
            .And.Contain("Widen the window", "and says what to do about it");
    }

    [Fact]
    public void ValidateSessionWindow_IncludesExactlyOneSession_WhenTheWindowExactlyContainsIt()
    {
        // The boundary the clipped-window refusal must not move: a window whose edges land EXACTLY on one
        // session's open and close still names that one session, not zero. Off-by-one at a session edge is
        // the classic defect a new zero-count guard could introduce.
        DateTimeOffset from = new(2026, 8, 3, 13, 30, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

        SessionWindowPlan plan = Guards().ValidateSessionWindow(from, to, Rth, Calendar);

        plan.TradeDates.Should().Equal([new DateOnly(2026, 8, 3)], "the window contains exactly one whole session");
    }

    [Fact]
    public void ValidateSessionWindow_RefusesWhenTheWindowEndsOneTickBeforeTheSessionCloses()
    {
        // One tick short of ValidateSessionWindow_IncludesExactlyOneSession_WhenTheWindowExactlyContainsIt's
        // window -- the session's own close is excluded by one tick, so the session is clipped and the count
        // drops from one straight to zero rather than to some smaller whole number. Pins the boundary from
        // the other side of the off-by-one this guard could get wrong.
        DateTimeOffset from = new(2026, 8, 3, 13, 30, 0, TimeSpan.Zero);
        DateTimeOffset to = new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero).AddTicks(-1);

        Action refuse = () => Guards().ValidateSessionWindow(from, to, Rth, Calendar);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("no whole rth session", "one tick short of the close is still clipped")
            .And.Contain(
                "nearest whole rth session is 2026-08-03",
                "and the nearest whole session is the very one the window just missed");
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

        Action refuse = () => Guards().ValidateSessionCount(UnsatisfiableCount, Rth, FridaysOnly(now), now);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("count 5000", "the refusal names the parameter and the value")
            .And.Contain(
                "than the calendar holds in the 20015 calendar days",
                "the cause is the sparse calendar, not a walk that stopped early -- the server DOES walk "
                + "the whole span, and a refusal that blames the span sends the reader to the wrong bug")
            .And.Contain("Ask for fewer", "and says what to do about it");
    }

    [Fact]
    public void ValidateSessionCount_RefusesANowPastTheCalendarHorizon_NamingNow()
    {
        // Two different faults arrive as the same exception type from the same call: a count the calendar
        // cannot satisfy, and an instant the calendar cannot express (the walk starts one day AHEAD of now's
        // market date, which at the top of the DateOnly range throws). Reporting the second as the first
        // tells a caller to ask for fewer sessions, which will not fix an argument that is out of the
        // calendar's reach -- so the instant is judged before the walk is entered at all.
        Action refuse = () => Guards().ValidateSessionCount(3, Rth, Calendar, DateTimeOffset.MaxValue);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("now", "the refusal names the argument that is out of reach")
            .And.Contain("session calendar can reason about", "and the bound it is past")
            .And.NotContain(
                "Ask for fewer",
                "an unrepresentable instant is not an unsatisfiable count, and a translation that cannot "
                + "tell them apart misreports one of them every time");
    }

    [Fact]
    public void ValidateSessionCount_RefusesACountWhoseBaseBucketsExceedThePass_BeforeAnyRead()
    {
        // THE CAP THE COUNT FORM WAS MISSING. MaxRows admits 5,000 sessions, and the service answers a count
        // with ONE covering base read from the first session's open to the last one's close -- so a count the
        // row cap allows can span more base buckets than a single gap-detection pass will enumerate, and
        // BarGapDetector.ExpectedBuckets faults on it AFTER the store has already been opened. About 3,720
        // `rth` sessions is where that starts at a 30-minute base; 4,000 is comfortably past it and still
        // well inside MaxRows.
        //
        // Judged here rather than in the tool, so every refusal on this surface still fires before the read.
        DateTimeOffset now = MarketClock.FromMarket(new DateOnly(2026, 8, 6), new TimeOnly(20, 0));

        Action refuse = () => Guards().ValidateSessionCount(4_000, Rth, Calendar, now);

        refuse.Should().Throw<McpException>().Which.Message
            .Should().Contain("count 4000 rth sessions", "the refusal names the parameter and the value")
            .And.Contain(
                "gap-detection pass",
                "and the bound it is over -- which is the base-bucket cap, not the row cap")
            .And.Contain(
                "Ask for fewer sessions.",
                "and the one remedy this caller has: there is no window and no resolution to narrow");
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

        for (int i = 0; i < SparseCalendarDays; i++)
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
