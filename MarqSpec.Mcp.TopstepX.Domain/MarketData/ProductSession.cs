namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The absolute UTC bounds of one trade date's product session — previous-evening open through
/// the trade date's close.
/// </summary>
/// <param name="TradeDate">The trade date the calendar assigns.</param>
/// <param name="OpenUtc">When the session opens, UTC.</param>
/// <param name="CloseUtc">When the session closes, UTC. The exclusive end of the session.</param>
public readonly record struct ProductSession(DateOnly TradeDate, DateTimeOffset OpenUtc, DateTimeOffset CloseUtc);

/// <summary>
/// Resolves the product session that contains an instant, and the one that most recently closed.
/// </summary>
/// <remarks>
/// <para>
/// Both answers ask <see cref="BarSessionCalendar"/> rather than restating its rules. A second
/// implementation of the weekend, the maintenance window, the holiday-suppresses-the-previous-evening
/// rule and a declared early close is a second place for them to be wrong. The scan is the same shape
/// <c>ReferenceTools</c> uses to find the next open: step, ask the calendar, then report the
/// session's own bounds rather than the probe that happened to land inside it.
/// </para>
/// <para>
/// A declared early close shortens only the session this type returns. Recorder health is that caller.
/// Bar expectation keeps the ordinary close and does not ask here.
/// </para>
/// <para>
/// Nothing here reads a clock. The instant is an argument, so the same inputs give the same
/// answer whenever it runs (ADR-0006).
/// </para>
/// </remarks>
public static class ProductSessions
{
    private static readonly TimeSpan _step = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _lookback = TimeSpan.FromDays(14);

    /// <summary>The session of one trade date.</summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns>The session. Both bounds are UTC.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calendar"/> is null.</exception>
    public static ProductSession For(BarSessionCalendar calendar, DateOnly tradeDate)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        DateTimeOffset open = MarketClock.FromMarket(tradeDate.AddDays(-1), calendar.SessionOpen).ToUniversalTime();
        DateTimeOffset close = MarketClock.FromMarket(tradeDate, calendar.SessionCloseOn(tradeDate)).ToUniversalTime();
        return new ProductSession(tradeDate, open, close);
    }

    /// <summary>
    /// The session that contains <paramref name="instant"/>, or <see langword="null"/> when the
    /// market is shut.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="instant">The instant.</param>
    /// <returns>The running session, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calendar"/> is null.</exception>
    public static ProductSession? Containing(BarSessionCalendar calendar, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (calendar.TradeDateFor(instant) is not { } tradeDate)
        {
            return null;
        }

        ProductSession session = For(calendar, tradeDate);
        return instant < session.CloseUtc ? session : null;
    }

    /// <summary>
    /// The session that closed at or before <paramref name="instant"/>, or <see langword="null"/>
    /// when one is still open or none closed inside the lookback.
    /// </summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="instant">The instant.</param>
    /// <returns>The most recently closed session, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="calendar"/> is null.</exception>
    /// <remarks>
    /// Bounded at a fortnight so a calendar that never closes returns null rather than spinning.
    /// A session that is still open is not this answer — <see cref="Containing"/> owns that.
    /// </remarks>
    public static ProductSession? LatestClosed(BarSessionCalendar calendar, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        if (calendar.TradeDateFor(instant) is { } tradeDate)
        {
            ProductSession running = For(calendar, tradeDate);
            return running.CloseUtc <= instant ? running : null;
        }

        DateTimeOffset limit = instant - _lookback;
        for (DateTimeOffset probe = instant; probe >= limit; probe -= _step)
        {
            if (calendar.TradeDateFor(probe) is not { } closedOn)
            {
                continue;
            }

            ProductSession session = For(calendar, closedOn);
            if (session.CloseUtc <= instant)
            {
                return session;
            }
        }

        return null;
    }
}
