namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The market's wall clock — US Central, the CME's timezone.
/// </summary>
/// <remarks>
/// <para>
/// Every session rule in this codebase is expressed in Central <b>wall-clock</b> time rather than a fixed UTC
/// offset, because that is how the exchange states them: the equity-index session closes at 16:00 Central both
/// in January and in July, and a rule written as "21:00 UTC" is silently wrong for half the year.
/// </para>
/// <para>
/// The IANA id is used rather than the Windows one; .NET resolves IANA ids on Windows too, and hard-coding
/// <c>"Central Standard Time"</c> would not resolve on Linux — which is where this runs in a container.
/// </para>
/// </remarks>
public static class MarketClock
{
    /// <summary>The market timezone (America/Chicago).</summary>
    public static readonly TimeZoneInfo MarketTimeZone = ResolveCentral();

    /// <summary>Converts an instant to market wall-clock time.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The same instant, expressed in the market timezone.</returns>
    public static DateTimeOffset ToMarket(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, MarketTimeZone);

    /// <summary>The market-local calendar date an instant falls on.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The market-local date.</returns>
    public static DateOnly MarketDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToMarket(instant).DateTime);

    /// <summary>The market-local time of day an instant falls on.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The market-local time of day.</returns>
    public static TimeOnly MarketTimeOfDay(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(ToMarket(instant).DateTime);

    /// <summary>
    /// Converts a market-local date and time back to an absolute instant.
    /// </summary>
    /// <param name="date">The market-local date.</param>
    /// <param name="time">The market-local time of day.</param>
    /// <returns>The instant.</returns>
    /// <remarks>
    /// On the spring-forward transition a wall-clock time can be skipped, and on the autumn transition it can
    /// occur twice. .NET resolves the ambiguous case to <b>standard</b> time; neither boundary falls inside a
    /// session close or reopen for the products handled here, so this does not need a policy of its own.
    /// </remarks>
    public static DateTimeOffset FromMarket(DateOnly date, TimeOnly time)
    {
        DateTime unspecified = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        TimeSpan offset = MarketTimeZone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    private static TimeZoneInfo ResolveCentral()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        }
        catch (TimeZoneNotFoundException)
        {
            // A Windows host without ICU still answers to the legacy id. Falling back is better than
            // failing to start over a timezone database detail.
            return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
        }
    }
}
