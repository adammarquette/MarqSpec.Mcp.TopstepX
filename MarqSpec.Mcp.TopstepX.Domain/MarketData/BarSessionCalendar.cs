using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Decides whether the venue is expected to have produced a bar for a given bucket — so a weekend, a daily
/// maintenance window, a session boundary or a declared holiday is never mistaken for a hole in the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is what makes cache-aside terminate.</b> Without it, "the store has no bar at 03:00 on Sunday"
/// and "the store is missing a bar the venue really published" are the same observation, so every cold read
/// re-requests the weekend from the gateway, forever, and gets an empty answer every time.
/// </para>
/// <para>
/// The model: CME equity-index futures run Sunday evening through Friday afternoon. A <b>trade date</b>'s
/// session opens the previous calendar evening at <see cref="SessionOpen"/> and closes at
/// <see cref="SessionClose"/> on the trade date itself, both in Central wall-clock time. So Saturday carries no
/// session at all, Sunday-evening buckets belong to <i>Monday</i>, and Friday evening does not reopen.
/// </para>
/// <para>
/// A declared holiday closes its own session outright, <b>and</b> suppresses the preceding evening's reopen —
/// the evening before a holiday belongs to the holiday's session, and that session does not happen. The
/// holiday's <i>own</i> evening still reopens, because that leg belongs to the next day, which trades.
/// </para>
/// <para>
/// <b>Known bound:</b> a bucket that would straddle the close is reported not-expected, which is exactly what
/// the gateway's <c>includePartialBar: false</c> produces. Sub-hour resolutions never approach that bound.
/// </para>
/// <para>
/// <b>An early close shortens the recorder-health session only.</b> <see cref="SessionCloseOn"/> is the close
/// <c>ProductSessions</c> asks for. <see cref="IsExpectedBucket"/> and <see cref="TradeDateFor"/> keep the
/// ordinary close, so the hours after a declared early close still look like missing bars. Changing that
/// would make the bar cache treat an afternoon the venue did publish as a hole, or the reverse, and the
/// health report is the reader that was wrong.
/// </para>
/// </remarks>
public sealed class BarSessionCalendar
{
    /// <summary>The default daily maintenance window between a session's close and the next session's open.</summary>
    public static readonly TimeSpan DefaultMaintenanceWindow = TimeSpan.FromHours(1);

    private readonly HashSet<DateOnly> _holidays;
    private readonly Dictionary<DateOnly, TimeOnly> _earlyCloses;

    /// <summary>Creates the calendar for a product's session.</summary>
    /// <param name="sessionClose">The daily session close, in Central wall-clock time.</param>
    /// <param name="holidays">Declared non-trading days; empty when none are declared.</param>
    /// <param name="maintenanceWindow">
    /// How long the venue is down between the close and the next open. Defaults to one hour.
    /// </param>
    /// <param name="earlyCloses">
    /// Trade dates whose session ends earlier than <paramref name="sessionClose"/>, in Central wall-clock
    /// time. Empty when none are declared. The next session still opens at <see cref="SessionOpen"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The maintenance window is not positive, or is a day or longer, or an early close is before 02:00
    /// Central or not strictly before the ordinary close.
    /// </exception>
    public BarSessionCalendar(
        TimeOnly sessionClose,
        IReadOnlyCollection<DateOnly> holidays,
        TimeSpan? maintenanceWindow = null,
        IReadOnlyDictionary<DateOnly, TimeOnly>? earlyCloses = null)
    {
        ArgumentNullException.ThrowIfNull(holidays);

        TimeSpan window = maintenanceWindow ?? DefaultMaintenanceWindow;
        if (window <= TimeSpan.Zero || window >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maintenanceWindow),
                window,
                "The maintenance window must be positive and shorter than a day.");
        }

        RefuseIfSpringForwardWouldShortenSession(sessionClose, window);
        RefuseEarlyCloses(sessionClose, earlyCloses);

        SessionClose = sessionClose;
        MaintenanceWindow = window;
        _holidays = [.. holidays];
        _earlyCloses = earlyCloses is null
            ? []
            : new Dictionary<DateOnly, TimeOnly>(earlyCloses);
    }

    private static void RefuseEarlyCloses(TimeOnly sessionClose, IReadOnlyDictionary<DateOnly, TimeOnly>? earlyCloses)
    {
        if (earlyCloses is null)
        {
            return;
        }

        foreach (KeyValuePair<DateOnly, TimeOnly> pair in earlyCloses)
        {
            if (pair.Value < new TimeOnly(2, 0) || pair.Value >= sessionClose)
            {
                throw new EarlyCloseException(
                    pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " "
                    + pair.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
                    + " is not before the ordinary session close.");
            }
        }
    }

    /// <summary>
    /// The trade date used to measure how long a session would be when spring-forward falls inside it.
    /// </summary>
    /// <remarks>
    /// US spring-forward is 02:00 Central on a Sunday; Monday 2030-03-11 is the example named in gh#613.
    /// </remarks>
    private static readonly DateOnly _springForwardExampleTradeDate = new(2030, 3, 11);

    /// <summary>
    /// When the session close is before 02:00 Central, the reopen lands before 03:00 and spring-forward can
    /// fall inside the session once a year.
    /// </summary>
    private static void RefuseIfSpringForwardWouldShortenSession(TimeOnly sessionClose, TimeSpan maintenanceWindow)
    {
        if (sessionClose >= new TimeOnly(2, 0))
        {
            return;
        }

        int shortenedMinutes = SessionLengthMinutes(
            _springForwardExampleTradeDate,
            sessionClose.Add(maintenanceWindow),
            sessionClose);
        int nominalMinutes = (int)(TimeSpan.FromDays(1) - maintenanceWindow).TotalMinutes;

        throw new ArgumentOutOfRangeException(
            nameof(sessionClose),
            sessionClose,
            "Session close '"
            + sessionClose.ToString("HH:mm", CultureInfo.InvariantCulture)
            + "' puts the spring-forward transition inside the session, which would be "
            + shortenedMinutes.ToString(CultureInfo.InvariantCulture)
            + " minutes long once a year rather than "
            + nominalMinutes.ToString(CultureInfo.InvariantCulture)
            + ".");
    }

    private static int SessionLengthMinutes(DateOnly tradeDate, TimeOnly sessionOpen, TimeOnly sessionClose)
    {
        DateTimeOffset open = MarketClock.FromMarket(tradeDate.AddDays(-1), sessionOpen).ToUniversalTime();
        DateTimeOffset close = MarketClock.FromMarket(tradeDate, sessionClose).ToUniversalTime();
        return (int)(close - open).TotalMinutes;
    }

    /// <summary>The daily session close, in Central wall-clock time.</summary>
    public TimeOnly SessionClose { get; }

    /// <summary>How long the venue is down between a close and the next open.</summary>
    public TimeSpan MaintenanceWindow { get; }

    /// <summary>When the next session opens, in Central wall-clock time — the close plus the maintenance window.</summary>
    public TimeOnly SessionOpen => SessionClose.Add(MaintenanceWindow);

    /// <summary>
    /// The close of <paramref name="tradeDate"/>, in Central wall-clock time. An ordinary day returns
    /// <see cref="SessionClose"/>; a declared early close returns that earlier time.
    /// </summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns>The close that date's session ends at.</returns>
    /// <remarks>
    /// Bar expectation does not call this. <see cref="ProductSessions"/> does, and recorder health is the
    /// caller of that type, so a declared early close shortens only the health report's session. The bar
    /// cache still measures the ordinary close.
    /// </remarks>
    public TimeOnly SessionCloseOn(DateOnly tradeDate) =>
        _earlyCloses.TryGetValue(tradeDate, out TimeOnly early) ? early : SessionClose;

    /// <summary>The declared non-trading days.</summary>
    public IReadOnlyCollection<DateOnly> Holidays => _holidays;

    /// <summary>
    /// Parses an operator-facing calendar, failing loudly on a malformed value rather than guessing.
    /// </summary>
    /// <param name="sessionClose">The session close, <c>HH:mm</c> in Central time.</param>
    /// <param name="holidays">The declared holidays, each <c>yyyy-MM-dd</c>.</param>
    /// <param name="maintenanceWindow">The maintenance window; defaults to one hour.</param>
    /// <param name="earlyCloses">
    /// Declared early closes, each <c>yyyy-MM-dd HH:mm</c> in Central wall-clock time. Empty when none.
    /// </param>
    /// <returns>The calendar.</returns>
    /// <exception cref="FormatException">The session close or a holiday is not in the expected shape.</exception>
    /// <exception cref="EarlyCloseException">An early close is not <c>yyyy-MM-dd HH:mm</c>, or it is not before the ordinary close.</exception>
    /// <remarks>
    /// Guessing here would be a silent, compounding error: this value decides what counts as missing data, so a
    /// misparsed close either makes the store look complete when it is not, or makes it re-fetch forever.
    /// </remarks>
    public static BarSessionCalendar Parse(
        string sessionClose,
        IReadOnlyCollection<string> holidays,
        TimeSpan? maintenanceWindow = null,
        IReadOnlyCollection<string>? earlyCloses = null)
    {
        ArgumentNullException.ThrowIfNull(sessionClose);
        ArgumentNullException.ThrowIfNull(holidays);

        if (!TimeOnly.TryParseExact(
                sessionClose, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly close))
        {
            throw new FormatException("Session close '" + sessionClose + "' is not in HH:mm form.");
        }

        List<DateOnly> parsed = [];
        foreach (string holiday in holidays)
        {
            if (!DateOnly.TryParseExact(
                    holiday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
            {
                throw new FormatException("Holiday '" + holiday + "' is not in yyyy-MM-dd form.");
            }

            parsed.Add(day);
        }

        return new BarSessionCalendar(close, parsed, maintenanceWindow, ParseEarlyCloses(earlyCloses));
    }

    private static Dictionary<DateOnly, TimeOnly> ParseEarlyCloses(IReadOnlyCollection<string>? entries)
    {
        Dictionary<DateOnly, TimeOnly> parsed = [];
        if (entries is null)
        {
            return parsed;
        }

        foreach (string entry in entries)
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !DateOnly.TryParseExact(
                    parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                || !TimeOnly.TryParseExact(
                    parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly close))
            {
                throw new EarlyCloseException("'" + entry + "' is not yyyy-MM-dd HH:mm.");
            }

            parsed[day] = close;
        }

        return parsed;
    }

    /// <summary>
    /// Whether the venue is expected to publish a bar that opens at <paramref name="bucketStart"/> and runs for
    /// <paramref name="barSize"/>.
    /// </summary>
    /// <param name="bucketStart">When the bucket opens.</param>
    /// <param name="barSize">The bar size.</param>
    /// <returns>
    /// <see langword="true"/> when a bar is expected; <see langword="false"/> for any non-session bucket.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="barSize"/> is not positive.</exception>
    public bool IsExpectedBucket(DateTimeOffset bucketStart, TimeSpan barSize)
    {
        if (barSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(barSize), barSize, "A bar size must be positive.");
        }

        if (TradeDateFor(bucketStart) is not { } tradeDate)
        {
            return false;
        }

        // The bucket must also CLOSE inside the session it opened in. A bar that would straddle the close is
        // never published as a final bar, so counting it as expected would report a permanent hole.
        DateTimeOffset close = MarketClock.FromMarket(tradeDate, SessionClose);
        return bucketStart + barSize <= close;
    }

    /// <summary>
    /// The trade date whose session contains <paramref name="instant"/>, or <see langword="null"/> when the
    /// market is shut — maintenance, weekend, or a declared holiday.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The trade date, or <see langword="null"/>.</returns>
    public DateOnly? TradeDateFor(DateTimeOffset instant)
    {
        DateOnly date = MarketClock.MarketDate(instant);
        TimeOnly time = MarketClock.MarketTimeOfDay(instant);

        DateOnly tradeDate;
        if (time < SessionClose)
        {
            // Before today's close: this belongs to today's session, which opened last evening.
            tradeDate = date;
        }
        else if (time >= SessionOpen)
        {
            // After the reopen: this is the evening leg of TOMORROW's session.
            tradeDate = date.AddDays(1);
        }
        else
        {
            return null; // Inside the maintenance window.
        }

        return IsTradingDay(tradeDate) ? tradeDate : null;
    }

    /// <summary>
    /// Whether a trade date has a session at all — weekdays that are not declared holidays.
    /// </summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns><see langword="true"/> when the date trades.</returns>
    /// <remarks>
    /// Saturday and Sunday are excluded as <i>trade dates</i>, which is a different statement from "nothing
    /// happens at the weekend": Sunday <i>evening</i> is the opening leg of Monday's session, and it is
    /// admitted because its trade date is Monday.
    /// </remarks>
    public bool IsTradingDay(DateOnly tradeDate) =>
        tradeDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && !_holidays.Contains(tradeDate);
}
