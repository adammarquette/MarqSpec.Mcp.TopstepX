namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Bollinger Bands — a simple moving average with a band at a fixed multiple of the rolling standard deviation.
/// </summary>
/// <remarks>
/// The multiple is fixed at <see cref="StandardDeviations"/> for the same reason MACD's fast and signal lengths
/// are fixed: the storage key is <c>(Indicator, Period)</c>, and a configurable multiple would be a third
/// parameter with nowhere honest to live. A 2-sigma and a 3-sigma band written under one key are
/// indistinguishable once stored.
/// </remarks>
public static class BollingerBands
{
    /// <summary>The band width in standard deviations, fixed at the conventional 2.</summary>
    public const int StandardDeviations = 2;

    /// <summary>
    /// The middle band — a simple moving average of the close.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The window length. The conventional value is 20.</param>
    /// <returns>One entry per bar, null until the window fills.</returns>
    public static IReadOnlyList<decimal?> Middle(IReadOnlyList<Bar> bars, int period) =>
        MovingAverages.Simple(bars, period);

    /// <summary>
    /// The upper band.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The window length.</param>
    /// <returns>One entry per bar, null until the window fills.</returns>
    public static IReadOnlyList<decimal?> Upper(IReadOnlyList<Bar> bars, int period) =>
        Band(bars, period, positive: true);

    /// <summary>
    /// The lower band.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The window length.</param>
    /// <returns>One entry per bar, null until the window fills.</returns>
    public static IReadOnlyList<decimal?> Lower(IReadOnlyList<Bar> bars, int period) =>
        Band(bars, period, positive: false);

    /// <summary>
    /// The population standard deviation of the close over a rolling window.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The window length.</param>
    /// <returns>One entry per bar, null until the window fills.</returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    /// <remarks>
    /// Population, not sample — Bollinger's own definition divides by <c>n</c>. Using the sample form would
    /// widen every band slightly and disagree with every charting package a reader might check against.
    /// </remarks>
    public static IReadOnlyList<decimal?> StandardDeviation(IReadOnlyList<Bar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorGuard.RequirePositivePeriod(period, nameof(period));
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));

        decimal?[] values = new decimal?[bars.Count];
        if (bars.Count < period)
        {
            return values;
        }

        for (int i = period - 1; i < bars.Count; i++)
        {
            decimal sum = 0m;
            for (int j = i - period + 1; j <= i; j++)
            {
                sum += bars[j].Close;
            }

            decimal mean = sum / period;

            decimal squares = 0m;
            for (int j = i - period + 1; j <= i; j++)
            {
                decimal deviation = bars[j].Close - mean;
                squares += deviation * deviation;
            }

            values[i] = DecimalMath.Sqrt(squares / period);
        }

        return values;
    }

    private static IReadOnlyList<decimal?> Band(IReadOnlyList<Bar> bars, int period, bool positive)
    {
        IReadOnlyList<decimal?> middle = Middle(bars, period);
        IReadOnlyList<decimal?> deviation = StandardDeviation(bars, period);

        decimal?[] band = new decimal?[middle.Count];
        for (int i = 0; i < middle.Count; i++)
        {
            if (middle[i] is { } m && deviation[i] is { } d)
            {
                decimal offset = StandardDeviations * d;
                band[i] = positive ? m + offset : m - offset;
            }
        }

        return band;
    }
}

/// <summary>The Bollinger middle band, as an <see cref="IIndicator"/>.</summary>
/// <param name="period">The window length; 20 conventionally.</param>
public sealed class BollingerMiddleIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>bb-middle</c>.</summary>
    public string Name => "bb-middle";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => BollingerBands.Middle(bars, Period);
}

/// <summary>The Bollinger upper band, as an <see cref="IIndicator"/>.</summary>
/// <param name="period">The window length; 20 conventionally.</param>
public sealed class BollingerUpperIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>bb-upper</c>.</summary>
    public string Name => "bb-upper";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => BollingerBands.Upper(bars, Period);
}

/// <summary>The Bollinger lower band, as an <see cref="IIndicator"/>.</summary>
/// <param name="period">The window length; 20 conventionally.</param>
public sealed class BollingerLowerIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>bb-lower</c>.</summary>
    public string Name => "bb-lower";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => BollingerBands.Lower(bars, Period);
}
