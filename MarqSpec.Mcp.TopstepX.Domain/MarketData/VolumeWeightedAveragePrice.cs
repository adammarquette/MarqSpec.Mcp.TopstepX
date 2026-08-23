namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Session-anchored VWAP — the volume-weighted average price since the current session opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>VWAP is only meaningful with an anchor</b>, and the anchor for intraday futures work is the session. A
/// "rolling VWAP" over the last N bars is a different statistic that traders do not read the same way, and one
/// computed over the whole loaded window would drift with how much history the caller happened to request —
/// making the value depend on the question rather than on the market.
/// </para>
/// <para>
/// This takes a <see cref="BarSessionCalendar"/> so it knows where a session begins. The calendar is itself a
/// pure, deterministic function of its configuration, so <see cref="Compute"/> stays a pure function of its
/// inputs and "rebuild = replay" still holds.
/// </para>
/// </remarks>
public static class VolumeWeightedAveragePrice
{
    /// <summary>
    /// Computes session-anchored VWAP for each bar.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="calendar">The session calendar that decides where each session begins.</param>
    /// <returns>
    /// One entry per bar. A bar the calendar places outside a session, and any bar in a session that has
    /// traded no volume at all, carries <see langword="null"/>.
    /// </returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    public static IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars, BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(calendar);
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        decimal?[] values = new decimal?[bars.Count];

        DateOnly? currentSession = null;
        decimal notional = 0m;
        long volume = 0L;

        for (int i = 0; i < bars.Count; i++)
        {
            Bar bar = bars[i];
            DateOnly? session = calendar.TradeDateFor(bar.OpenTime);

            if (session is null)
            {
                // Outside a session. Leave the accumulator alone rather than resetting it: a maintenance
                // window sits in the middle of nothing, but a stray out-of-session bar between two in-session
                // ones must not silently restart the day's average.
                continue;
            }

            if (currentSession != session)
            {
                currentSession = session;
                notional = 0m;
                volume = 0L;
            }

            // The typical price, not the close: VWAP weights where trade actually happened across the bar.
            decimal typical = (bar.High + bar.Low + bar.Close) / 3m;
            notional += typical * bar.Volume;
            volume += bar.Volume;

            // A session that has printed no volume has no volume-weighted price. Zero would be a lie and the
            // typical price would be an unweighted average wearing VWAP's name.
            values[i] = volume > 0 ? notional / volume : null;
        }

        return values;
    }
}

/// <summary>Session-anchored VWAP, as an <see cref="IIndicator"/>.</summary>
/// <param name="calendar">The session calendar that decides where each session begins.</param>
/// <remarks>
/// <see cref="Period"/> is <c>0</c> because VWAP takes no period — it is anchored, not windowed. The value
/// still participates in the storage key, so VWAP rows sit at <c>("vwap", 0)</c> and cannot collide with a
/// windowed indicator.
/// </remarks>
public sealed class VwapIndicator(BarSessionCalendar calendar) : IIndicator
{
    private readonly BarSessionCalendar _calendar =
        calendar ?? throw new ArgumentNullException(nameof(calendar));

    /// <summary>The stored name, <c>vwap</c>.</summary>
    public string Name => "vwap";

    /// <summary>Always <c>0</c> — VWAP is anchored to the session, not windowed over a period.</summary>
    public int Period => 0;

    /// <summary>
    /// Always <c>1</c>. VWAP has a value from the session's first bar; what it needs is not history but the
    /// session's own start, which the calendar supplies.
    /// </summary>
    public int WarmupBars => 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) =>
        VolumeWeightedAveragePrice.Compute(bars, _calendar);
}
