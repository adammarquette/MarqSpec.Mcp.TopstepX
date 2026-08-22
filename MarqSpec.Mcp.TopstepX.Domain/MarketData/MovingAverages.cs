namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Simple and exponential moving averages over closes.
/// </summary>
public static class MovingAverages
{
    /// <summary>
    /// Simple moving average of the close.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The window length.</param>
    /// <returns>One entry per bar, <see langword="null"/> until index <c>period - 1</c>.</returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    public static IReadOnlyList<decimal?> Simple(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorGuard.RequirePositivePeriod(period, nameof(period));
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));

        decimal?[] values = new decimal?[bars.Count];
        if (bars.Count < period)
        {
            return values;
        }

        // A rolling sum rather than an inner loop: O(n) instead of O(n*period), and because every term is
        // added and subtracted exactly once in decimal arithmetic, it is also exact rather than merely close.
        decimal window = 0m;
        for (int i = 0; i < bars.Count; i++)
        {
            window += bars[i].Close;
            if (i >= period)
            {
                window -= bars[i - period].Close;
            }

            if (i >= period - 1)
            {
                values[i] = window / period;
            }
        }

        return values;
    }

    /// <summary>
    /// Exponential moving average of the close, seeded from the simple average of the first window.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The window length; the smoothing factor is <c>2 / (period + 1)</c>.</param>
    /// <returns>One entry per bar, <see langword="null"/> until index <c>period - 1</c>.</returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    /// <remarks>
    /// Seeding from the SMA of the first window is the convention charting packages use. Seeding from the
    /// first close instead is also defensible, but it is a <i>different series</i> for the first several
    /// hundred bars, and an agent comparing this number against a chart would find they disagree.
    /// </remarks>
    public static IReadOnlyList<decimal?> Exponential(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorGuard.RequirePositivePeriod(period, nameof(period));
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));

        decimal?[] values = new decimal?[bars.Count];
        if (bars.Count < period)
        {
            return values;
        }

        decimal seed = 0m;
        for (int i = 0; i < period; i++)
        {
            seed += bars[i].Close;
        }

        decimal current = seed / period;
        values[period - 1] = current;

        decimal alpha = 2m / (period + 1);
        for (int i = period; i < bars.Count; i++)
        {
            current += alpha * (bars[i].Close - current);
            values[i] = current;
        }

        return values;
    }

    /// <summary>
    /// Exponential moving average over an arbitrary value series that may be sparse at the front.
    /// </summary>
    /// <param name="source">The series; leading <see langword="null"/> entries are the warm-up of whatever produced it.</param>
    /// <param name="period">The window length.</param>
    /// <returns>One entry per input, null until the source has warmed up <i>and</i> this window is satisfied.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    /// <remarks>
    /// MACD's signal line is an EMA of the MACD line, which is itself null until its own slow window fills.
    /// This overload exists so that composition does not have to fabricate a value for the null prefix — the
    /// warm-ups stack rather than silently starting the signal line early on invented data.
    /// </remarks>
    public static IReadOnlyList<decimal?> ExponentialOf(IReadOnlyList<decimal?> source, int period)
    {
        ArgumentNullException.ThrowIfNull(source);
        IndicatorGuard.RequirePositivePeriod(period, nameof(period));

        decimal?[] values = new decimal?[source.Count];

        int first = -1;
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].HasValue)
            {
                first = i;
                break;
            }
        }

        if (first < 0 || source.Count - first < period)
        {
            return values;
        }

        decimal seed = 0m;
        for (int i = first; i < first + period; i++)
        {
            seed += source[i]!.Value;
        }

        decimal current = seed / period;
        int seedIndex = first + period - 1;
        values[seedIndex] = current;

        decimal alpha = 2m / (period + 1);
        for (int i = seedIndex + 1; i < source.Count; i++)
        {
            if (source[i] is not { } value)
            {
                // A hole after warm-up is not something this can smooth over; carrying the last value forward
                // would invent data. Stop rather than fabricate.
                break;
            }

            current += alpha * (value - current);
            values[i] = current;
        }

        return values;
    }
}

/// <summary>Simple moving average of the close, as an <see cref="IIndicator"/>.</summary>
/// <param name="period">The window length.</param>
public sealed class SmaIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>sma</c>.</summary>
    public string Name => "sma";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => MovingAverages.Simple(bars, Period);
}

/// <summary>Exponential moving average of the close, as an <see cref="IIndicator"/>.</summary>
/// <param name="period">The window length.</param>
public sealed class EmaIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>ema</c>.</summary>
    public string Name => "ema";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => MovingAverages.Exponential(bars, Period);
}
