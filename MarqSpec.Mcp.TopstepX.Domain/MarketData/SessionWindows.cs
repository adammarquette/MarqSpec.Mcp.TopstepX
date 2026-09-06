using System.Globalization;
using System.Text.RegularExpressions;

namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Turns a <see cref="SessionDefinition"/> into the absolute UTC bounds of one trade date's session, and
/// answers which trade dates a UTC window or an instant admits.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is expressed as an <b>offset from the trade date's open</b> rather than as a wall-clock
/// time, because that is the coordinate the exchange's own session runs in. In it, <c>asia</c> (17:00 -&gt;
/// 02:00) is simply 0h -&gt; 9h and needs no "spans midnight" special case, while a definition that would
/// straddle the maintenance window runs backwards and is refused by the same comparison.
/// </para>
/// <para>
/// Nothing here reads a clock: <see cref="LastClosedTradeDates"/> takes "now" as an argument, so the same
/// inputs give the same answer whenever it runs (ADR-0006).
/// </para>
/// </remarks>
public static partial class SessionWindows
{
    /// <summary>
    /// Checks a definition against the calendar it will be resolved on, throwing on the first rule it breaks.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="calendar">The calendar the definition is stated against.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The definition breaks a rule; the message names which one.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The base resolution has to divide the hour because Central is a whole-hour UTC offset: a wall-clock
    /// boundary on a grid that divides the hour lands on the stored UTC grid under both CST and CDT. A
    /// 120-minute base would not — 17:00 Central is 22:00Z in summer and 23:00Z in winter, and only one of
    /// those is on a 120-minute UTC grid.
    /// </para>
    /// <para>
    /// <b>The boundary rule is stated in wall-clock, not in the offset coordinate the rest of this file uses.</b>
    /// <see cref="BarGapDetector.AlignUp"/> anchors buckets on a fixed UTC-midnight grid, so what has to hold
    /// is that each boundary's own instant lands on that grid: each boundary's <b>minutes past the hour is a
    /// multiple of the base, which divides 60</b>, so the boundary lands on the UTC bucket grid under both
    /// offsets. Measuring the same thing as an offset from the session open agrees only when the open is
    /// itself on the grid — true of the shipped 16:00 close, false of an operator's 13:20 one, where
    /// 14:20 -&gt; 13:20 at a 60-minute base reads as a clean 0h -&gt; 23h and resolves to a window whose first
    /// forty minutes no bucket covers.
    /// </para>
    /// <para>
    /// The direction, the session-length bound and "at least one base bucket long" stay in the offset
    /// coordinate, because those are statements about the session rather than about the stored grid.
    /// </para>
    /// </remarks>
    public static void Validate(SessionDefinition definition, BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(calendar);

        if (definition.Name is null || !NamePattern().IsMatch(definition.Name))
        {
            throw new ArgumentException(
                "A session name is a storage key: it must match ^[a-z][a-z0-9-]{0,15}$, and '"
                + definition.Name + "' does not.",
                nameof(definition));
        }

        if (definition.BaseResolutionMinutes is < 1 or > 60 || 60 % definition.BaseResolutionMinutes != 0)
        {
            throw new ArgumentException(
                "A session's base resolution must be between 1 and 60 minutes and divide 60, so its"
                + " wall-clock boundaries land on the stored UTC grid under both standard and daylight time; "
                + definition.BaseResolutionMinutes.ToString(CultureInfo.InvariantCulture) + " does not.",
                nameof(definition));
        }

        TimeSpan start = OffsetFromOpen(calendar, definition.StartCentral);
        TimeSpan end = OffsetFromOpen(calendar, definition.EndCentral);
        TimeSpan sessionLength = TimeSpan.FromDays(1) - calendar.MaintenanceWindow;

        if (start >= end || end > sessionLength)
        {
            throw new ArgumentException(
                "A session must run forwards inside the trading session it sits in: measured from the open,"
                + " '" + definition.Name + "' runs " + start.ToString(null, CultureInfo.InvariantCulture)
                + " to " + end.ToString(null, CultureInfo.InvariantCulture) + ", outside (0, "
                + sessionLength.ToString(null, CultureInfo.InvariantCulture) + "].",
                nameof(definition));
        }

        // The grid rule is asked of the WALL-CLOCK boundaries, not of their offsets from the open: buckets are
        // anchored on a fixed UTC-midnight grid, and the two coordinates coincide only when the open is itself
        // on that grid. Central is a whole-hour UTC offset and the base divides 60, so a boundary lands on the
        // stored grid under both CST and CDT exactly when its minutes past the hour are a multiple of the base.
        int baseMinutes = definition.BaseResolutionMinutes;
        if (definition.StartCentral.Minute % baseMinutes != 0 || definition.EndCentral.Minute % baseMinutes != 0)
        {
            TimeOnly offending = definition.StartCentral.Minute % baseMinutes != 0
                ? definition.StartCentral
                : definition.EndCentral;

            throw new ArgumentException(
                "Both of a session's boundaries must land on the stored UTC bucket grid: the base divides 60"
                + " and Central is a whole-hour UTC offset, so a boundary's minutes past the hour must be a"
                + " multiple of the base. '" + definition.Name + "' has "
                + offending.ToString("HH:mm", CultureInfo.InvariantCulture) + " on a "
                + baseMinutes.ToString(CultureInfo.InvariantCulture) + "-minute base, which does not.",
                nameof(definition));
        }

        // Unreachable while both boundaries are on the grid — the gap between two grid points is a multiple of
        // the base, and the ordering rule above already made it positive. It stays because the rule it states
        // is the session's, not the grid's, and a later change to either rule must not silently admit a
        // session shorter than the bar it is built from.
        TimeSpan @base = TimeSpan.FromMinutes(baseMinutes);
        if (end - start < @base)
        {
            throw new ArgumentException(
                "A session must be at least one base bucket long, and '" + definition.Name + "' runs "
                + (end - start).ToString(null, CultureInfo.InvariantCulture) + " on a "
                + baseMinutes.ToString(CultureInfo.InvariantCulture) + "-minute base.",
                nameof(definition));
        }
    }

    /// <summary>
    /// The absolute window a trade date's session occupies, or <see langword="null"/> when the calendar
    /// carries no such session.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="definition">The definition.</param>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns>
    /// The half-open window <c>[open, close)</c>, or <see langword="null"/>. Both bounds come from
    /// <see cref="MarketClock.FromMarket"/> and so carry the <b>market's</b> UTC offset, not zero — the same
    /// instants a UTC window would name, written in the coordinate the session was stated in. A caller
    /// putting them on the wire or into a field named for UTC normalises first, the way
    /// <see cref="SessionBarAggregator"/> does with <c>ToUniversalTime</c>: <see cref="DateTimeOffset"/>
    /// equality compares instants, so the difference is invisible to a test and visible to a reader.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Each boundary is placed on the previous calendar day when its wall-clock time is at or after the
    /// reopen — that is the evening leg, and the reason Sunday 17:00 belongs to Monday. The candidate is then
    /// handed back to <see cref="BarSessionCalendar.TradeDateFor"/>, the way
    /// <see cref="SessionLevels.SessionOpenFor"/> does, so a definition the calendar disowns yields
    /// <see langword="null"/> rather than a boundary nothing agrees with. The close itself is accepted
    /// explicitly: it is the exclusive end of a half-open range, and the calendar reads it as maintenance.
    /// </remarks>
    public static BarRange? WindowFor(
        BarSessionCalendar calendar, SessionDefinition definition, DateOnly tradeDate)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(definition);

        if (!calendar.IsTradingDay(tradeDate))
        {
            return null;
        }

        DateTimeOffset start = BoundaryFor(calendar, tradeDate, definition.StartCentral);
        DateTimeOffset end = BoundaryFor(calendar, tradeDate, definition.EndCentral);
        if (end <= start || calendar.TradeDateFor(start) != tradeDate)
        {
            return null;
        }

        DateTimeOffset sessionClose = MarketClock.FromMarket(tradeDate, calendar.SessionClose);
        return end == sessionClose || calendar.TradeDateFor(end) == tradeDate
            ? new BarRange(start, end)
            : null;
    }

    /// <summary>
    /// Every trade date whose <b>whole</b> session window lies inside a UTC window.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="definition">The definition.</param>
    /// <param name="utcWindow">The UTC window.</param>
    /// <returns>The trade dates, ascending; empty when none qualify.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// "Opens at or after the start, closes at or before the end" is the same bound
    /// <see cref="BarGapDetector.ExpectedBuckets"/> applies to a bucket. A session only partly inside the
    /// window is excluded rather than truncated: a session bar built from part of a session is a wrong number,
    /// not a rough one.
    /// </remarks>
    public static IReadOnlyList<DateOnly> TradeDatesIn(
        BarSessionCalendar calendar, SessionDefinition definition, BarRange utcWindow)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(utcWindow);

        if (utcWindow.IsEmpty)
        {
            return [];
        }

        // A session can open the previous calendar evening and close on the trade date, so the candidate span
        // is the window's own market dates widened by a day at each end.
        DateOnly first = MarketClock.MarketDate(utcWindow.Start);
        DateOnly last = MarketClock.MarketDate(utcWindow.End).AddDays(1);

        List<DateOnly> tradeDates = [];
        for (DateOnly candidate = first; candidate <= last; candidate = candidate.AddDays(1))
        {
            if (WindowFor(calendar, definition, candidate) is { } window
                && window.Start >= utcWindow.Start
                && window.End <= utcWindow.End)
            {
                tradeDates.Add(candidate);
            }
        }

        return tradeDates;
    }

    /// <summary>
    /// The most recent trade dates whose session had closed at or before <paramref name="now"/>.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="definition">The definition.</param>
    /// <param name="now">The instant to look back from.</param>
    /// <param name="count">How many trade dates to find.</param>
    /// <returns>The trade dates, ascending.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> is not positive, or the calendar carries fewer closed sessions than that
    /// inside the bounded walk.
    /// </exception>
    /// <remarks>
    /// A session closing exactly at <paramref name="now"/> is closed — the window is half-open, so its last
    /// bucket has already ended. The walk is bounded so a calendar of holidays refuses rather than scanning
    /// backwards forever; the caller can then say the data is not there, which is the honest answer.
    /// </remarks>
    public static IReadOnlyList<DateOnly> LastClosedTradeDates(
        BarSessionCalendar calendar, SessionDefinition definition, DateTimeOffset now, int count)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // Start one day ahead of the market date: the session of the date after `now` can already have closed
        // when `now` sits in the evening leg that opened it.
        DateOnly cursor = MarketClock.MarketDate(now).AddDays(1);

        // The floor is inclusive, so the walk examines `span` days, not `span - 1`. Stated once and used by
        // both the bound and the refusal, because a message quoting a different number than the code walked
        // is a message that sends the next reader looking for a bug that is not there.
        int span = (count * 4) + 15;
        DateOnly floor = cursor.AddDays(-(span - 1));

        List<DateOnly> found = [];
        for (DateOnly candidate = cursor; candidate >= floor && found.Count < count; candidate = candidate.AddDays(-1))
        {
            if (WindowFor(calendar, definition, candidate) is { } window && window.End <= now)
            {
                found.Add(candidate);
            }
        }

        if (found.Count < count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                "The calendar carries only " + found.Count.ToString(CultureInfo.InvariantCulture)
                + " closed '" + definition.Name + "' session(s) in the "
                + span.ToString(CultureInfo.InvariantCulture)
                + " calendar days before " + now.ToString("O", CultureInfo.InvariantCulture) + ".");
        }

        found.Reverse();
        return found;
    }

    /// <summary>
    /// How far into the trading session a Central wall-clock time falls — the coordinate every rule here is
    /// stated in.
    /// </summary>
    private static TimeSpan OffsetFromOpen(BarSessionCalendar calendar, TimeOnly time)
    {
        TimeSpan offset = time.ToTimeSpan() - calendar.SessionOpen.ToTimeSpan();
        return offset >= TimeSpan.Zero ? offset : offset + TimeSpan.FromDays(1);
    }

    /// <summary>
    /// Places one Central wall-clock boundary on a calendar day: the previous one when it belongs to the
    /// evening leg, the trade date itself otherwise.
    /// </summary>
    /// <remarks>
    /// A daylight-saving Sunday can never receive an evening-leg boundary: the transition is at 02:00 and
    /// <see cref="BarSessionCalendar.SessionOpen"/> — the close plus the maintenance window, 17:00 on the
    /// shipped calendar — is many hours past it, so the wall-clock time this resolves is neither skipped nor
    /// ambiguous. That guarantee is the <i>calendar's</i>, not this method's: an operator closing before
    /// about 03:00 would put the reopen inside the transition, and <see cref="MarketClock.FromMarket"/>
    /// resolves a skipped or doubled wall-clock time to the standard offset without saying so.
    /// </remarks>
    private static DateTimeOffset BoundaryFor(BarSessionCalendar calendar, DateOnly tradeDate, TimeOnly central)
    {
        DateOnly day = central >= calendar.SessionOpen ? tradeDate.AddDays(-1) : tradeDate;
        return MarketClock.FromMarket(day, central);
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
