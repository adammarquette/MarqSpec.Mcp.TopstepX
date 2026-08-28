namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Whether a requested resolution produces a bucket that overhangs a session close.
/// </summary>
/// <remarks>
/// <para>
/// A bar's width is not on the bar. The interval to its successor is not the width of a period's
/// <i>last</i> bar — that successor is in the next session, an unrelated maintenance window away —
/// which is why <see cref="ILevelMethod.Detect"/> must not infer a resolution from the series it is
/// handed. This guard takes <c>resolutionMinutes</c> as an argument, which is what the tool boundary
/// has and a method does not (gh#259).
/// </para>
/// <para>
/// The rule is plain: a bucket that would not close inside the session it opened in overhangs, and
/// so does a bucket that starts outside a session and runs into one. Refusing every such bucket,
/// rather than reasoning about which overhangs contaminate, is the decision the fourth routed
/// finding settled on. A 12-hour bar aligned to the reopen (overhanging only the maintenance
/// window) is therefore refused with the 07:00/19:00 alignment that actually contaminates.
/// </para>
/// </remarks>
public static class SessionBucketGuard
{
    /// <summary>The reason a session-anchored method records when this guard refuses it.</summary>
    public const string RefusalReason = "refused: buckets overhang a session close";

    /// <summary>
    /// Whether <paramref name="resolutionMinutes"/> produces a bucket that overhangs a close.
    /// </summary>
    /// <param name="resolutionMinutes">The bar size, in minutes. Must be positive.</param>
    /// <param name="calendar">The session calendar.</param>
    /// <param name="bars">
    /// The series, when on hand. Each bar is checked at the stated width, so an alignment off the
    /// reopen (Sunday 07:00 covering Monday's open) is refused even when a reopen-aligned walk
    /// would not have produced that bar.
    /// </param>
    /// <returns><see langword="true"/> when session-anchored methods must refuse.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolution is not positive.</exception>
    public static bool OverhangsClose(
        int resolutionMinutes,
        BarSessionCalendar calendar,
        IReadOnlyList<Bar>? bars = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (resolutionMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolutionMinutes),
                resolutionMinutes,
                "A resolution must be positive; a bar of no width cannot overhang a close.");
        }

        TimeSpan size = TimeSpan.FromMinutes(resolutionMinutes);

        // Walk one trading day's session from its own open in steps of the stated resolution. A
        // store that never held the last, overhanging bucket would otherwise look clean at 240
        // minutes: 17:00, 21:00, 01:00, 05:00, 09:00 all close inside the session, and 13:00–17:00
        // is the one that does not.
        if (ProbeTradeDate(calendar, bars) is { } tradeDate
            && SessionLevels.SessionOpenFor(calendar, tradeDate) is { } open)
        {
            DateTimeOffset close = MarketClock.FromMarket(tradeDate, calendar.SessionClose);
            for (DateTimeOffset t = open; t < close; t += size)
            {
                if (!calendar.IsExpectedBucket(t, size))
                {
                    return true;
                }
            }
        }

        if (bars is null)
        {
            return false;
        }

        foreach (Bar bar in bars)
        {
            if (!calendar.IsExpectedBucket(bar.OpenTime, size))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A trading day to walk, taken from the series when it names one, otherwise the first weekday
    /// the calendar agrees trades — never "today".
    /// </summary>
    private static DateOnly? ProbeTradeDate(BarSessionCalendar calendar, IReadOnlyList<Bar>? bars)
    {
        if (bars is not null)
        {
            for (int i = bars.Count - 1; i >= 0; i--)
            {
                if (calendar.TradeDateFor(bars[i].OpenTime) is { } fromSeries)
                {
                    return fromSeries;
                }
            }
        }

        // A Monday far from any DST transition and from this repository's usual August fixtures.
        // Stepping forward covers a calendar that happens to shut the first few weekdays.
        DateOnly probe = new(2026, 1, 5);
        for (int i = 0; i < 10; i++, probe = probe.AddDays(1))
        {
            if (calendar.IsTradingDay(probe))
            {
                return probe;
            }
        }

        return null;
    }
}
