namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Rolling VWAP — the volume-weighted average price over the trailing <c>period</c> bars.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>different calculation from the session-anchored <c>vwap</c></b>
/// (<see cref="VolumeWeightedAveragePrice"/>), not merely a parameterised version of it: VWAP is only
/// meaningful with an anchor, and traders do not read a lookback window the same way they read
/// "since the session opened". Different calculation, different name — <c>vwap-rolling</c> — so the two never
/// share a storage key.
/// </para>
/// <para>
/// Because the window is a fixed count of bars rather than a session boundary, this needs no
/// <see cref="BarSessionCalendar"/>: it is windowed exactly like <see cref="MovingAverages.Simple"/>, just
/// weighted by volume instead of an unweighted average of closes.
/// </para>
/// </remarks>
public static class RollingVolumeWeightedAveragePrice
{
    /// <summary>
    /// Computes the rolling VWAP for each bar.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The trailing window length, in bars.</param>
    /// <returns>
    /// One entry per bar. <see langword="null"/> until the window has <paramref name="period"/> bars behind
    /// it, and <see langword="null"/> whenever the window's total volume is zero — a volume-weighted average
    /// of nothing is not a price, and substituting the typical price or zero would report one anyway.
    /// </returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    public static IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorGuard.RequirePositivePeriod(period, nameof(period));
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        decimal?[] values = new decimal?[bars.Count];
        if (bars.Count < period)
        {
            return values;
        }

        // A rolling sum, as MovingAverages.Simple uses: each bar's notional and volume enter and leave the
        // window exactly once, so this is O(n) and exact in decimal rather than merely close.
        decimal notional = 0m;
        long volume = 0L;
        for (int i = 0; i < bars.Count; i++)
        {
            Bar bar = bars[i];
            decimal typical = (bar.High + bar.Low + bar.Close) / 3m;
            notional += typical * bar.Volume;
            volume += bar.Volume;

            if (i >= period)
            {
                Bar leaving = bars[i - period];
                decimal leavingTypical = (leaving.High + leaving.Low + leaving.Close) / 3m;
                notional -= leavingTypical * leaving.Volume;
                volume -= leaving.Volume;
            }

            if (i >= period - 1)
            {
                values[i] = volume > 0 ? notional / volume : null;
            }
        }

        return values;
    }
}

/// <summary>Rolling VWAP over the trailing <see cref="Period"/> bars, as an <see cref="IIndicator"/>.</summary>
/// <param name="period">The trailing window length, in bars.</param>
public sealed class RollingVwapIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>vwap-rolling</c>.</summary>
    public string Name => "vwap-rolling";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) =>
        RollingVolumeWeightedAveragePrice.Compute(bars, Period);
}
