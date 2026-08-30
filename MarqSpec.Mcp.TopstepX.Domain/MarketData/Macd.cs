namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Moving Average Convergence Divergence — the line, its signal, and the histogram between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>On parameterisation and storage identity.</b> MACD takes three numbers, and the storage key is
/// <c>(Indicator, Period)</c> — two. Rather than let a third parameter hide inside a name that does not
/// mention it, the <b>fast</b> and <b>signal</b> lengths are fixed at their conventional 12 and 9
/// (<see cref="FastPeriod"/>, <see cref="SignalPeriod"/>) and only the slow length is the period. So a stored
/// <c>("macd", 26)</c> row is unambiguously MACD(12, 26, 9), and there is no way to write two different
/// parameterisations into the same key.
/// </para>
/// <para>
/// If a configurable fast or signal length is ever wanted, the change is to put it <i>in the name</i> — not to
/// add a config knob behind the existing one. A knob that silently repartitions a stored series is how a chart
/// ends up showing two parameterisations spliced together with no seam visible.
/// </para>
/// </remarks>
public static class Macd
{
    /// <summary>The fast EMA length, fixed at the conventional 12.</summary>
    public const int FastPeriod = 12;

    /// <summary>The signal EMA length, fixed at the conventional 9.</summary>
    public const int SignalPeriod = 9;

    /// <summary>
    /// Computes the MACD line — the fast EMA minus the slow EMA of the close.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="slowPeriod">The slow EMA length. The conventional value is 26.</param>
    /// <returns>One entry per bar, null until the slow EMA has warmed up.</returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The slow period is not greater than <see cref="FastPeriod"/>.</exception>
    public static IReadOnlyList<decimal?> Line(IReadOnlyList<Bar> bars, int slowPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (slowPeriod <= FastPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slowPeriod),
                slowPeriod,
                "The slow period must be greater than the fast period (" + FastPeriod + ").");
        }

        IReadOnlyList<decimal?> fast = MovingAverages.Exponential(bars, FastPeriod);
        IReadOnlyList<decimal?> slow = MovingAverages.Exponential(bars, slowPeriod);

        decimal?[] line = new decimal?[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            // Both must be warm. The fast EMA warms first, and subtracting a warm fast from a null slow would
            // have to invent the slow value — so the line starts when the SLOWER of the two starts.
            if (fast[i] is { } f && slow[i] is { } s)
            {
                line[i] = f - s;
            }
        }

        return line;
    }

    /// <summary>
    /// Computes the signal line — a <see cref="SignalPeriod"/> EMA of the MACD line.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="slowPeriod">The slow EMA length.</param>
    /// <returns>One entry per bar, null until both the line and this EMA have warmed up.</returns>
    public static IReadOnlyList<decimal?> Signal(IReadOnlyList<Bar> bars, int slowPeriod) =>
        MovingAverages.ExponentialOf(Line(bars, slowPeriod), SignalPeriod);

    /// <summary>
    /// Computes the histogram — the MACD line minus its signal.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="slowPeriod">The slow EMA length.</param>
    /// <returns>One entry per bar, null until both inputs are warm.</returns>
    public static IReadOnlyList<decimal?> Histogram(IReadOnlyList<Bar> bars, int slowPeriod)
    {
        IReadOnlyList<decimal?> line = Line(bars, slowPeriod);
        IReadOnlyList<decimal?> signal = MovingAverages.ExponentialOf(line, SignalPeriod);

        decimal?[] histogram = new decimal?[line.Count];
        for (int i = 0; i < line.Count; i++)
        {
            if (line[i] is { } l && signal[i] is { } s)
            {
                histogram[i] = l - s;
            }
        }

        return histogram;
    }
}

/// <summary>The MACD line, as an <see cref="IIndicator"/>.</summary>
/// <param name="slowPeriod">The slow EMA length; 26 conventionally.</param>
public sealed class MacdLineIndicator(int slowPeriod) : IIndicator
{
    /// <summary>The stored name, <c>macd</c>.</summary>
    public string Name => "macd";

    /// <inheritdoc />
    public int Period { get; } = slowPeriod;

    /// <inheritdoc />
    public int WarmupBars => Period;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => Macd.Line(bars, Period);
}

/// <summary>The MACD signal line, as an <see cref="IIndicator"/>.</summary>
/// <param name="slowPeriod">The slow EMA length; 26 conventionally.</param>
public sealed class MacdSignalIndicator(int slowPeriod) : IIndicator
{
    /// <summary>The stored name, <c>macd-signal</c>.</summary>
    public string Name => "macd-signal";

    /// <inheritdoc />
    public int Period { get; } = slowPeriod;

    /// <inheritdoc />
    /// <remarks>
    /// The line first produces a value after <see cref="Period"/> bars; the signal is a
    /// <see cref="Macd.SignalPeriod"/> EMA of that line. The two windows share a bar, so the
    /// minimum is their sum minus one.
    /// </remarks>
    public int WarmupBars => Period + Macd.SignalPeriod - 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => Macd.Signal(bars, Period);
}

/// <summary>The MACD histogram, as an <see cref="IIndicator"/>.</summary>
/// <param name="slowPeriod">The slow EMA length; 26 conventionally.</param>
public sealed class MacdHistogramIndicator(int slowPeriod) : IIndicator
{
    /// <summary>The stored name, <c>macd-histogram</c>.</summary>
    public string Name => "macd-histogram";

    /// <inheritdoc />
    public int Period { get; } = slowPeriod;

    /// <inheritdoc />
    /// <remarks>
    /// The histogram is warm on the same bar as the signal, so the minimum is the same
    /// <c>Period + SignalPeriod - 1</c> — not the sum of the two periods, which double-counts
    /// the bar they share.
    /// </remarks>
    public int WarmupBars => Period + Macd.SignalPeriod - 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => Macd.Histogram(bars, Period);
}
