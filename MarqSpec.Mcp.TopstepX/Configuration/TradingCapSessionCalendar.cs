using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Topstep session day boundary for daily contract caps — rolls at 5:00 PM America/Chicago (DST-aware).
/// </summary>
public static class TradingCapSessionCalendar
{
    /// <summary>
    /// Start of the current session day in UTC: the most recent 5:00 PM Central before <paramref name="instant"/>.
    /// </summary>
    public static DateTimeOffset SessionDayStartUtc(DateTimeOffset instant)
    {
        DateTimeOffset central = TimeZoneInfo.ConvertTime(instant, MarketClock.MarketTimeZone);
        DateTime centralDate = central.Date;
        DateTime sessionStartLocal = centralDate.AddHours(17);
        if (central.LocalDateTime < sessionStartLocal)
        {
            sessionStartLocal = centralDate.AddDays(-1).AddHours(17);
        }

        DateTime unspecified = DateTime.SpecifyKind(sessionStartLocal, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, MarketClock.MarketTimeZone);
    }
}
