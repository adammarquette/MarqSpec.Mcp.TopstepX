using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One backfill window and the venue pages inside it (gh#899).</summary>
/// <param name="Range">The window, at most <see cref="BarBackfillPlan.WindowBars"/> bars.</param>
/// <param name="Pages">The pages, each at most <see cref="BarBackfillPlan.PageBars"/> bars.</param>
public sealed record BarBackfillWindow(BarRange Range, IReadOnlyList<BarRange> Pages);

/// <summary>
/// Splits a backfill into 5,000-bar windows of 1,000-bar pages, and stops at the measured history edge.
/// </summary>
public static class BarBackfillPlan
{
    /// <summary>The driver's window. #899's 5,000 is this, not a venue page.</summary>
    public const int WindowBars = 5_000;

    /// <summary>One venue page. The same cap as <see cref="BarCacheService.VenuePageSizeBars"/>.</summary>
    public const int PageBars = BarCacheService.VenuePageSizeBars;

    /// <summary>
    /// How far minute bars were measured to reach. This is a sim-tier figure (gh#494). The live tier
    /// has not been measured.
    /// </summary>
    public const int MinuteDepthDays = 63;

    /// <summary>
    /// How far hour bars were measured to reach, or to listing. This is a sim-tier figure (gh#494).
    /// The live tier has not been measured.
    /// </summary>
    public const int HourDepthDays = 365;

    /// <summary>The oldest instant this resolution may ask for.</summary>
    /// <param name="resolutionMinutes">The bar size.</param>
    /// <param name="now">The run's clock.</param>
    /// <returns>The edge. Minute bars use 63 days. An hour or larger uses one year.</returns>
    public static DateTimeOffset Edge(int resolutionMinutes, DateTimeOffset now)
    {
        int days = resolutionMinutes >= 60 ? HourDepthDays : MinuteDepthDays;
        return now - TimeSpan.FromDays(days);
    }

    /// <summary>The windows to ask for, oldest first, none of them older than the measured edge.</summary>
    /// <param name="resolutionMinutes">The bar size.</param>
    /// <param name="now">The run's clock. The open bucket at this instant is not requested.</param>
    /// <param name="notBeforeUtc">
    /// A later start. A value older than <see cref="Edge"/> is clamped forward to the edge.
    /// </param>
    /// <returns>The windows. Empty when the clamped start is already at <paramref name="now"/>.</returns>
    public static IReadOnlyList<BarBackfillWindow> Windows(
        int resolutionMinutes,
        DateTimeOffset now,
        DateTimeOffset? notBeforeUtc)
    {
        TimeSpan bar = TimeSpan.FromMinutes(resolutionMinutes);
        DateTimeOffset edge = AlignDown(Edge(resolutionMinutes, now), bar);
        DateTimeOffset start = edge;
        if (notBeforeUtc is DateTimeOffset floor)
        {
            DateTimeOffset aligned = AlignDown(floor, bar);
            if (aligned > start)
            {
                start = aligned;
            }
        }

        DateTimeOffset end = AlignDown(now, bar);
        if (end <= start)
        {
            return [];
        }

        TimeSpan window = TimeSpan.FromTicks(bar.Ticks * WindowBars);
        TimeSpan page = TimeSpan.FromTicks(bar.Ticks * PageBars);
        List<BarBackfillWindow> windows = [];
        for (DateTimeOffset from = start; from < end;)
        {
            DateTimeOffset to = end - from <= window ? end : from + window;
            windows.Add(new BarBackfillWindow(new BarRange(from, to), Pages(from, to, page)));
            from = to;
        }

        return windows;
    }

    private static List<BarRange> Pages(DateTimeOffset start, DateTimeOffset end, TimeSpan page)
    {
        List<BarRange> pages = [];
        for (DateTimeOffset from = start; from < end;)
        {
            DateTimeOffset to = end - from <= page ? end : from + page;
            pages.Add(new BarRange(from, to));
            from = to;
        }

        return pages;
    }

    private static DateTimeOffset AlignDown(DateTimeOffset instant, TimeSpan bar)
    {
        long size = bar.Ticks;
        long aligned = instant.UtcTicks - (instant.UtcTicks % size);
        return new DateTimeOffset(aligned, TimeSpan.Zero);
    }
}
