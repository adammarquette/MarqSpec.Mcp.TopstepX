namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// How long a last or a session volume may ride on later bid and ask ticks (gh#983).
/// </summary>
public static class LatestQuoteCarry
{
    /// <summary>
    /// A carried last or volume older than this is dropped. A CME session change drops it sooner.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether a last or volume seen at <paramref name="seenAt"/> in <paramref name="seenSession"/>
    /// may still be reported at <paramref name="now"/>.
    /// </summary>
    /// <param name="seenAt">When the field was carried by a quote or a trade. Null means unknown.</param>
    /// <param name="seenSession">The trade date of that event. Null means the market was shut.</param>
    /// <param name="now">The clock the new quote is being judged on.</param>
    /// <param name="nowSession">The trade date of <paramref name="now"/>, from the session calendar.</param>
    /// <returns>
    /// <see langword="true"/> when the field is still inside the same session and inside
    /// <see cref="MaxAge"/>.
    /// </returns>
    public static bool Keep(
        DateTimeOffset? seenAt,
        DateOnly? seenSession,
        DateTimeOffset now,
        DateOnly? nowSession)
    {
        if (seenAt is null || seenSession != nowSession)
        {
            return false;
        }

        if (now < seenAt.Value)
        {
            return true;
        }

        return now - seenAt.Value <= MaxAge;
    }
}
