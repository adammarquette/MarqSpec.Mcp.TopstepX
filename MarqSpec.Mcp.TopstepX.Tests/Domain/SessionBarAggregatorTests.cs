using System.Globalization;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.Domain;

/// <summary>
/// One session bar per trade date, and the completeness guard that decides whether there is one at all.
/// </summary>
/// <remarks>
/// <para>
/// Every number here is hand-derived. The base bars follow one pattern throughout — bar <c>i</c> carries
/// <c>Open 100+i, High 105+i, Low 95+i, Close 101+i, Volume 10</c> — so the session's open is the first bar's
/// open, its high is the <b>last</b> bar's high, its low is the <b>first</b> bar's low and its close is the
/// last bar's close. A monotone pattern is deliberate: an aggregate that took the wrong end of the series
/// cannot accidentally agree with one that took the right end.
/// </para>
/// <para>
/// The absences are the point of the type. A session bar built from part of a session is a wrong number
/// wearing an ordinary face — a session bar missing its opening hour reads as an ordinary day that simply
/// opened somewhere else. So the outcome is either complete or absent with a stated reason, and there is no
/// third shape.
/// </para>
/// </remarks>
public sealed class SessionBarAggregatorTests
{
    private const string Front = "U26";
    private const string Next = "Z26";

    private static BarSessionCalendar Calendar() => BarSessionCalendar.Parse("16:00", []);

    private static SessionDefinition Shipped(string name) =>
        SessionDefinition.Defaults.Single(definition => definition.Name == name);

    /// <summary>An absolute instant, stated in UTC — the only form a session bar's bounds are handed out in.</summary>
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static BarRange Window(BarSessionCalendar calendar, SessionDefinition definition, DateOnly tradeDate) =>
        SessionWindows.WindowFor(calendar, definition, tradeDate)
        ?? throw new InvalidOperationException("The fixture asked for a session the calendar does not carry.");

    /// <summary>
    /// The base bars of one session: <paramref name="count"/> buckets from the window's start, one base
    /// resolution apart.
    /// </summary>
    private static List<Bar> Session(
        BarRange window, int baseMinutes, int count, string? contractId = Front) =>
        [.. Enumerable.Range(0, count).Select(i => new Bar(
            window.Start.AddMinutes(baseMinutes * i).ToUniversalTime(),
            100m + i,
            105m + i,
            95m + i,
            101m + i,
            10,
            contractId))];

    [Fact]
    public void Aggregate_ProducesOneBar_WhenEveryRthBucketIsStored()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18); // Tuesday, CDT (UTC-5).

        // 08:30 to 15:00 Central is 13:30Z to 20:00Z: 6.5 hours, 13 buckets of 30 minutes, 08:30 .. 14:30.
        List<Bar> bars = Session(Window(calendar, rth, tradeDate), 30, 13);

        IReadOnlyList<SessionBarOutcome> outcomes =
            SessionBarAggregator.Aggregate(bars, calendar, rth, [tradeDate]);

        outcomes.Should().Equal(SessionBarOutcome.Present(new SessionBar(
            tradeDate,
            Utc(2026, 8, 18, 13, 30),
            Utc(2026, 8, 18, 20, 0),
            100m,  // the first bucket's open
            117m,  // 105 + 12, the last bucket's high
            95m,   // 95 + 0, the first bucket's low
            113m,  // 101 + 12, the last bucket's close
            130,   // 13 buckets of 10
            Front,
            13)));

        // The bounds are named OpenUtc/CloseUtc, so they are handed out at zero offset rather than at
        // Central's -05:00 — DateTimeOffset equality compares instants and would not catch the difference.
        outcomes[0].Bar!.OpenUtc.Offset.Should().Be(TimeSpan.Zero);
        outcomes[0].Bar!.CloseUtc.Offset.Should().Be(TimeSpan.Zero);
        outcomes[0].IsPresent.Should().BeTrue();
    }

    [Fact]
    public void Aggregate_IsAbsentIncomplete_WhenOneBucketIsMissing()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18);

        List<Bar> bars = Session(Window(calendar, rth, tradeDate), 30, 13);
        bars.RemoveAt(5); // 11:00 Central never arrived.

        SessionBarAggregator.Aggregate(bars, calendar, rth, [tradeDate])
            .Should().Equal(SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.Incomplete, 13, 1));
    }

    [Fact]
    public void Aggregate_IsAbsentIncomplete_WhenTheCountMatchesButABucketIsOffGrid()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18);

        List<Bar> bars = Session(Window(calendar, rth, tradeDate), 30, 13);
        bars.RemoveAt(5);

        // 16:30 Central — inside the maintenance hour, a bucket the calendar never expected but the store can
        // nonetheless be holding (gh#412). Counting bars would find 13 and call the session complete.
        bars.Add(new Bar(Utc(2026, 8, 18, 21, 30), 200m, 205m, 195m, 201m, 10, Front));

        SessionBarAggregator.Aggregate(bars, calendar, rth, [tradeDate])
            .Should().Equal(SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.Incomplete, 13, 1));
    }

    [Fact]
    public void Aggregate_IgnoresABarTheCalendarDoesNotExpect()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition full = Shipped("full");
        DateOnly tradeDate = new(2026, 8, 18);

        // 17:00 Central Monday to 16:00 Central Tuesday is 22:00Z Monday to 21:00Z Tuesday: 23 hourly buckets.
        List<Bar> bars = Session(Window(calendar, full, tradeDate), 60, 23);

        // 16:30 Central Monday, half an hour before the session opened. It sits in neither the window nor the
        // calendar's grid, and its 999 volume must not reach the session bar.
        bars.Insert(0, new Bar(Utc(2026, 8, 17, 21, 30), 500m, 505m, 495m, 501m, 999, Front));

        SessionBarAggregator.Aggregate(bars, calendar, full, [tradeDate])
            .Should().Equal(SessionBarOutcome.Present(new SessionBar(
                tradeDate,
                Utc(2026, 8, 17, 22, 0),
                Utc(2026, 8, 18, 21, 0),
                100m,
                127m,  // 105 + 22
                95m,
                123m,  // 101 + 22
                230,   // 23 buckets of 10 — the extra bar's 999 is not summed
                Front,
                23)));
    }

    [Fact]
    public void Aggregate_SpansMidnight_ForTheAsiaSessionOfAMonday()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition asia = Shipped("asia");
        DateOnly tradeDate = new(2026, 8, 17); // Monday: its session opens Sunday evening.

        // 17:00 Central Sunday to 02:00 Central Monday is 22:00Z Sunday to 07:00Z Monday: 9 hours, 18 buckets.
        List<Bar> bars = Session(Window(calendar, asia, tradeDate), 30, 18);

        SessionBarAggregator.Aggregate(bars, calendar, asia, [tradeDate])
            .Should().Equal(SessionBarOutcome.Present(new SessionBar(
                tradeDate,
                Utc(2026, 8, 16, 22, 0),
                Utc(2026, 8, 17, 7, 0),
                100m,
                122m,  // 105 + 17
                95m,
                118m,  // 101 + 17
                180,   // 18 buckets of 10
                Front,
                18)));
    }

    [Fact]
    public void Aggregate_ShiftsUtcBoundsAcrossADaylightChange_AndKeepsTheBucketCount()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition full = Shipped("full");
        DateOnly beforeTheChange = new(2026, 10, 30); // Friday, still CDT (UTC-5).
        DateOnly afterTheChange = new(2026, 11, 2);   // Monday; CST (UTC-6) since Sunday 02:00.

        List<Bar> bars =
        [
            .. Session(Window(calendar, full, beforeTheChange), 60, 23),
            .. Session(Window(calendar, full, afterTheChange), 60, 23)
        ];

        IReadOnlyList<SessionBarOutcome> outcomes =
            SessionBarAggregator.Aggregate(bars, calendar, full, [beforeTheChange, afterTheChange]);

        // The same wall-clock session, the same 23 buckets, an hour later in UTC on the far side of the
        // change. A window derived by adding a fixed offset would keep the bounds and lose an hour.
        outcomes[0].Bar!.OpenUtc.Should().Be(Utc(2026, 10, 29, 22, 0));
        outcomes[0].Bar!.CloseUtc.Should().Be(Utc(2026, 10, 30, 21, 0));
        outcomes[0].Bar!.BaseBucketCount.Should().Be(23);

        outcomes[1].Bar!.OpenUtc.Should().Be(Utc(2026, 11, 1, 23, 0));
        outcomes[1].Bar!.CloseUtc.Should().Be(Utc(2026, 11, 2, 22, 0));
        outcomes[1].Bar!.BaseBucketCount.Should().Be(23);
    }

    [Fact]
    public void Aggregate_IsAbsentSpansRoll_WhenTheContractChangesInsideTheSession()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18);

        // Every bucket is stored, so counting alone would call this complete. Half of them came from the
        // contract behind, which does not trade at the front month's price (ADR-0011).
        List<Bar> bars =
        [
            .. Session(Window(calendar, rth, tradeDate), 30, 13)
                .Select((bar, i) => bar with { ContractId = i < 7 ? Front : Next })
        ];

        SessionBarAggregator.Aggregate(bars, calendar, rth, [tradeDate])
            .Should().Equal(SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.SpansRoll, 13, 0));
    }

    [Fact]
    public void Aggregate_IsAbsentProvenanceUnknown_WhenNoBaseBarRecordsAContract()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18);

        // Rows written before the provenance was captured. Nothing is missing and nothing is spliced, but the
        // session bar cannot say which contract it belongs to, and one that cannot is unusable.
        List<Bar> bars = Session(Window(calendar, rth, tradeDate), 30, 13, contractId: null);

        SessionBarAggregator.Aggregate(bars, calendar, rth, [tradeDate])
            .Should().Equal(SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.ProvenanceUnknown, 13, 0));
    }

    [Fact]
    public void Aggregate_ReturnsOneOutcomePerRequestedTradeDate_InOrder()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly monday = new(2026, 8, 17);
        DateOnly tuesday = new(2026, 8, 18);

        List<Bar> bars = Session(Window(calendar, rth, monday), 30, 13);

        IReadOnlyList<SessionBarOutcome> outcomes =
            SessionBarAggregator.Aggregate(bars, calendar, rth, [monday, tuesday]);

        outcomes.Should().HaveCount(2);
        outcomes[0].IsPresent.Should().BeTrue();
        outcomes[0].Bar!.TradeDate.Should().Be(monday);
        outcomes[1].Should().Be(SessionBarOutcome.Absent(tuesday, SessionBarAbsence.Incomplete, 13, 13));
    }

    [Fact]
    public void Aggregate_RefusesAnUnorderedSeries()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18);

        List<Bar> bars = Session(Window(calendar, rth, tradeDate), 30, 13);
        (bars[3], bars[4]) = (bars[4], bars[3]);

        Action act = () => SessionBarAggregator.Aggregate(bars, calendar, rth, [tradeDate]);

        act.Should().Throw<ArgumentException>().WithMessage("*ascending*");
    }

    [Fact]
    public void Aggregate_RefusesATradeDateTheCalendarDoesNotTrade()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");

        // Saturday carries no session at all. That is a caller bug, not an absent bar: an absence is a
        // statement about a session that exists, and inventing one for Saturday would say nothing.
        Action act = () => SessionBarAggregator.Aggregate([], calendar, rth, [new DateOnly(2026, 8, 15)]);

        act.Should().Throw<ArgumentException>().WithMessage("*2026-08-15*");
    }

    [Fact]
    public void Aggregate_IsAbsent_WhenTheCalendarExpectsNoBucketAtAll()
    {
        BarSessionCalendar calendar = Calendar();
        DateOnly tradeDate = new(2026, 8, 18);

        // A definition SessionWindows.Validate would refuse: a half-hour window on a 60-minute base leaves
        // the calendar no bucket to expect. The window resolves, so the aggregation runs — and answers with
        // an absence rather than a bar assembled from nothing, or an index off the end of an empty series.
        SessionDefinition tooShort = new("tooshort", new TimeOnly(8, 30), new TimeOnly(9, 0), 60);

        SessionBarAggregator.Aggregate([], calendar, tooShort, [tradeDate])
            .Should().Equal(SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.Incomplete, 0, 0));
    }

    [Fact]
    public void Aggregate_NeverProducesAPartialBar()
    {
        BarSessionCalendar calendar = Calendar();
        SessionDefinition rth = Shipped("rth");
        DateOnly tradeDate = new(2026, 8, 18);
        List<Bar> complete = Session(Window(calendar, rth, tradeDate), 30, 13);

        // Every one of the 8,191 proper subsets of the session, the empty one included. A partial bar is not
        // a rough answer, it is a wrong one, so the only subset that produces a bar is the whole session.
        for (int mask = 0; mask < (1 << 13) - 1; mask++)
        {
            List<Bar> subset = [.. complete.Where((_, i) => (mask & (1 << i)) != 0)];

            IReadOnlyList<SessionBarOutcome> outcomes =
                SessionBarAggregator.Aggregate(subset, calendar, rth, [tradeDate]);

            outcomes.Should().ContainSingle();
            outcomes[0].Should().Be(
                SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.Incomplete, 13, 13 - subset.Count),
                "subset " + mask.ToString(CultureInfo.InvariantCulture) + " is missing a bucket");
        }
    }
}
