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
    /// The base resolution has to divide the hour because Central is a whole-hour UTC offset: a wall-clock
    /// boundary on a grid that divides the hour lands on the stored UTC grid under both CST and CDT. A
    /// 120-minute base would not — 17:00 Central is 22:00Z in summer and 23:00Z in winter, and only one of
    /// those is on a 120-minute UTC grid.
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

        TimeSpan @base = TimeSpan.FromMinutes(definition.BaseResolutionMinutes);
        if (start.Ticks % @base.Ticks != 0 || end.Ticks % @base.Ticks != 0 || end - start < @base)
        {
            throw new ArgumentException(
                "Both of a session's boundaries must sit on its base-resolution grid, and '"
                + definition.Name + "' has at least one that does not.",
                nameof(definition));
        }
    }

    /// <summary>
    /// The UTC window a trade date's session occupies, or <see langword="null"/> when the calendar carries no
    /// such session.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="definition">The definition.</param>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns>The half-open window <c>[open, close)</c> in UTC, or <see langword="null"/>.</returns>
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
        DateOnly floor = cursor.AddDays(-((count * 4) + 14));

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
                + ((count * 4) + 14).ToString(CultureInfo.InvariantCulture)
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
    private static DateTimeOffset BoundaryFor(BarSessionCalendar calendar, DateOnly tradeDate, TimeOnly central)
    {
        DateOnly day = central >= calendar.SessionOpen ? tradeDate.AddDays(-1) : tradeDate;
        return MarketClock.FromMarket(day, central);
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
