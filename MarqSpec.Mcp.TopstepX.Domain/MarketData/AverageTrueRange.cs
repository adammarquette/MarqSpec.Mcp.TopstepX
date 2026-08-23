namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Average True Range, Wilder's smoothing.
/// </summary>
/// <remarks>
/// ATR is the volatility unit the rest of this codebase measures in — key-level zone widths and their
/// significance scores are expressed in ATR multiples precisely so a score compares across instruments and
/// across volatility regimes. A raw point distance does not: two points is a wide zone on ES and noise on NQ.
/// </remarks>
public static class AverageTrueRange
{
    /// <summary>
    /// Computes ATR for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The smoothing period. Wilder's default is 14.</param>
    /// <returns>
    /// One entry per bar, <see langword="null"/> until index <paramref name="period"/>. True range needs a
    /// previous close, so the first bar can never carry a value and the seed needs <c>period + 1</c> bars.
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
        if (bars.Count <= period)
        {
            return values;
        }

        // Seed: the simple mean of the first `period` true ranges, starting at index 1 because true range is
        // defined against the previous close.
        decimal seedTotal = 0m;
        for (int i = 1; i <= period; i++)
        {
            seedTotal += TrueRange(bars[i], bars[i - 1]);
        }

        decimal current = seedTotal / period;
        values[period] = current;

        for (int i = period + 1; i < bars.Count; i++)
        {
            // Wilder: the new observation takes a 1/period weight, the running value keeps the rest.
            current = ((current * (period - 1)) + TrueRange(bars[i], bars[i - 1])) / period;
            values[i] = current;
        }

        return values;
    }

    /// <summary>
    /// True range for one bar against its predecessor.
    /// </summary>
    /// <param name="bar">The bar.</param>
    /// <param name="previous">The bar before it.</param>
    /// <returns>The true range.</returns>
    /// <remarks>
    /// The two gap terms are what make this "true" range rather than the bar's own high-low: an overnight gap
    /// moves price without trading through it, and a measure that ignored that would read a violent open as a
    /// quiet bar.
    /// </remarks>
    public static decimal TrueRange(Bar bar, Bar previous)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(previous);

        decimal previousClose = previous.Close;
        decimal ownRange = bar.High - bar.Low;
        decimal upFromClose = Math.Abs(bar.High - previousClose);
        decimal downFromClose = Math.Abs(bar.Low - previousClose);
        return Math.Max(ownRange, Math.Max(upFromClose, downFromClose));
    }
}

/// <summary>The <see cref="IIndicator"/> face of <see cref="AverageTrueRange"/>.</summary>
/// <param name="period">The smoothing period.</param>
public sealed class AtrIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>atr</c>.</summary>
    public string Name => "atr";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period + 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => AverageTrueRange.Compute(bars, Period);
}
