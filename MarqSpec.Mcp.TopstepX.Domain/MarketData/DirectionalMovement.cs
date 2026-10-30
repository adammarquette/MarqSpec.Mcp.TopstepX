namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Wilder's Directional Movement — the +DI and −DI lines, and the ADX that measures the spread between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this answers that the rest of the catalogue cannot: is there a trend worth not fading?</b> Every
/// other series here reads stretch or momentum, and a mean-reversion setup reading only those cannot tell a
/// dip inside a rally from a dip inside a range — it fades both, and the rally takes the stop (gh#670).
/// </para>
/// <para>
/// <b>ADX is direction-blind on purpose.</b> It rises on a strong move either way, so it says *how much*
/// trend and never *which way*; +DI and −DI carry the side. Reading ADX alone as bullish is the classic
/// misuse — a collapse and a melt-up score the same.
/// </para>
/// <para>
/// <b>Smoothing is Wilder's, the same trap already documented for ATR and RSI.</b> The seed is a simple mean
/// of the first <c>period</c> observations and every value after it carries the previous one at
/// <c>(period − 1) / period</c>. That is not an EMA of the conventional <c>2 / (period + 1)</c>, and a
/// library that substitutes one reads roughly half as smooth at the same period.
/// </para>
/// <para>
/// <b>Period is the only parameter, and that is a storage decision.</b> The key is
/// <c>(Indicator, Period)</c>, so a second knob would make two parameterisations indistinguishable once
/// stored. ADX conventionally shares the DI period and does so here.
/// </para>
/// </remarks>
public static class DirectionalMovement
{
    /// <summary>
    /// Computes +DI for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The Wilder smoothing period. The conventional default is 14.</param>
    /// <returns>
    /// One entry per bar, <see langword="null"/> until index <paramref name="period"/>, and
    /// <see langword="null"/> wherever the smoothed true range is zero and the ratio has no denominator.
    /// </returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    public static IReadOnlyList<decimal?> PlusDi(IReadOnlyList<Bar> bars, int period) =>
        Indicators(bars, period).Plus;

    /// <summary>
    /// Computes −DI for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The Wilder smoothing period. The conventional default is 14.</param>
    /// <returns>
    /// One entry per bar, <see langword="null"/> until index <paramref name="period"/>, and
    /// <see langword="null"/> wherever the smoothed true range is zero and the ratio has no denominator.
    /// </returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    public static IReadOnlyList<decimal?> MinusDi(IReadOnlyList<Bar> bars, int period) =>
        Indicators(bars, period).Minus;

    /// <summary>
    /// Computes ADX for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The Wilder smoothing period. The conventional default is 14.</param>
    /// <returns>
    /// One entry per bar, <see langword="null"/> until index <c>2 × period − 1</c> — ADX smooths a value
    /// that is itself smoothed, so it warms up twice.
    /// </returns>
    /// <exception cref="ArgumentException">The bars are not in strictly ascending time order.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    /// <remarks>
    /// A bar whose DX cannot be measured does not merely go null — it <b>restarts the warm-up</b>. ADX is a
    /// running value, and resuming the old one across a hole would carry a number derived from before the
    /// break while looking like an unbroken reading.
    /// </remarks>
    public static IReadOnlyList<decimal?> Adx(IReadOnlyList<Bar> bars, int period)
    {
        (IReadOnlyList<decimal?> plus, IReadOnlyList<decimal?> minus) = Indicators(bars, period);

        decimal?[] values = new decimal?[plus.Count];
        decimal seedTotal = 0m;
        int seedCount = 0;
        decimal current = 0m;
        bool seeded = false;

        for (int i = 0; i < values.Length; i++)
        {
            decimal? dx = DirectionalIndex(plus[i], minus[i]);

            if (dx is null)
            {
                seedTotal = 0m;
                seedCount = 0;
                seeded = false;
                continue;
            }

            if (seeded)
            {
                // Wilder: the new observation takes a 1/period weight, the running value keeps the rest.
                current = ((current * (period - 1)) + dx.Value) / period;
                values[i] = current;
                continue;
            }

            seedTotal += dx.Value;
            seedCount++;

            if (seedCount == period)
            {
                current = seedTotal / period;
                values[i] = current;
                seeded = true;
            }
        }

        return values;
    }

    /// <summary>
    /// The directional index — the absolute DI spread as a percentage of their sum.
    /// </summary>
    /// <param name="plus">The +DI, or null.</param>
    /// <param name="minus">The −DI, or null.</param>
    /// <returns>The DX, or null where it has no denominator.</returns>
    /// <remarks>
    /// The true range cancels out of this ratio — both DI carry it as their denominator — so DX depends only
    /// on how lopsided the directional movement was, not on how large it was.
    /// </remarks>
    private static decimal? DirectionalIndex(decimal? plus, decimal? minus)
    {
        if (plus is null || minus is null)
        {
            return null;
        }

        decimal sum = plus.Value + minus.Value;

        // Both DI at zero means the window recorded no directional movement at all. Zero is a REAL DX
        // elsewhere — it is what a perfect DI cross reads — so answering 0 here would be indistinguishable
        // from a measured balance between two live sides.
        return sum == 0m ? null : 100m * Math.Abs(plus.Value - minus.Value) / sum;
    }

    /// <summary>
    /// Both directional indicators in one pass — they share the smoothed true range they divide by.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The Wilder smoothing period.</param>
    /// <returns>The +DI and −DI series, aligned one-to-one with the bars.</returns>
    private static (IReadOnlyList<decimal?> Plus, IReadOnlyList<decimal?> Minus) Indicators(
        IReadOnlyList<Bar> bars,
        int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorGuard.RequirePositivePeriod(period, nameof(period));
        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));
        IndicatorGuard.RequireSingleContract(bars, nameof(bars));

        decimal?[] plus = new decimal?[bars.Count];
        decimal?[] minus = new decimal?[bars.Count];

        if (bars.Count <= period)
        {
            return (plus, minus);
        }

        // Seed: simple means of the first `period` observations, starting at index 1 because both true range
        // and directional movement are defined against the previous bar.
        decimal rangeTotal = 0m;
        decimal upTotal = 0m;
        decimal downTotal = 0m;

        for (int i = 1; i <= period; i++)
        {
            rangeTotal += AverageTrueRange.TrueRange(bars[i], bars[i - 1]);
            (decimal up, decimal down) = Movement(bars[i], bars[i - 1]);
            upTotal += up;
            downTotal += down;
        }

        decimal range = rangeTotal / period;
        decimal upSmoothed = upTotal / period;
        decimal downSmoothed = downTotal / period;
        Assign(plus, minus, period, range, upSmoothed, downSmoothed);

        for (int i = period + 1; i < bars.Count; i++)
        {
            (decimal up, decimal down) = Movement(bars[i], bars[i - 1]);

            range = ((range * (period - 1)) + AverageTrueRange.TrueRange(bars[i], bars[i - 1])) / period;
            upSmoothed = ((upSmoothed * (period - 1)) + up) / period;
            downSmoothed = ((downSmoothed * (period - 1)) + down) / period;

            Assign(plus, minus, i, range, upSmoothed, downSmoothed);
        }

        return (plus, minus);
    }

    /// <summary>Writes one bar's pair of directional indicators, or leaves both absent.</summary>
    /// <param name="plus">The +DI series being filled.</param>
    /// <param name="minus">The −DI series being filled.</param>
    /// <param name="index">The bar index.</param>
    /// <param name="range">The smoothed true range at that bar.</param>
    /// <param name="up">The smoothed +DM at that bar.</param>
    /// <param name="down">The smoothed −DM at that bar.</param>
    /// <remarks>
    /// A zero smoothed range is a series that did not move at all, and the ratio has no denominator. Both
    /// sides go absent together: one of them reported while the other is null would read as a one-sided
    /// market rather than as an unmeasurable one.
    /// </remarks>
    private static void Assign(
        decimal?[] plus,
        decimal?[] minus,
        int index,
        decimal range,
        decimal up,
        decimal down)
    {
        if (range == 0m)
        {
            return;
        }

        // Multiplied before dividing: 100 × 3 / 10 is exact where 3 / 10 × 100 rounds first.
        plus[index] = 100m * up / range;
        minus[index] = 100m * down / range;
    }

    /// <summary>
    /// Wilder's directional movement for one bar against its predecessor.
    /// </summary>
    /// <param name="bar">The bar.</param>
    /// <param name="previous">The bar before it.</param>
    /// <returns>The +DM and −DM, at most one of which is non-zero.</returns>
    /// <remarks>
    /// Only the <b>larger</b> of the two moves counts, and only when it is outward: a bar that both rose and
    /// fell against its predecessor — an outside bar — records movement on one side only, because a bar that
    /// counted both would read as simultaneous pressure in both directions and flatten DX toward zero.
    /// </remarks>
    private static (decimal Plus, decimal Minus) Movement(Bar bar, Bar previous)
    {
        decimal up = bar.High - previous.High;
        decimal down = previous.Low - bar.Low;

        return (up > down && up > 0m ? up : 0m, down > up && down > 0m ? down : 0m);
    }
}

/// <summary>The <see cref="IIndicator"/> face of <see cref="DirectionalMovement.Adx"/>.</summary>
/// <param name="period">The Wilder smoothing period.</param>
public sealed class AdxIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>adx</c>.</summary>
    public string Name => "adx";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <summary>
    /// Twice the period: ADX smooths a value that is itself smoothed, so it warms up twice over.
    /// </summary>
    /// <remarks>
    /// A caller that reached back only <c>Period + 1</c> — what the DI beside it need — would get an ADX
    /// series whose whole leading half is null and read it as a hole in the data.
    /// </remarks>
    public int WarmupBars => Period * 2;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => DirectionalMovement.Adx(bars, Period);
}

/// <summary>The <see cref="IIndicator"/> face of <see cref="DirectionalMovement.PlusDi"/>.</summary>
/// <param name="period">The Wilder smoothing period.</param>
public sealed class PlusDiIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>plus-di</c>.</summary>
    public string Name => "plus-di";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period + 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) =>
        DirectionalMovement.PlusDi(bars, Period);
}

/// <summary>The <see cref="IIndicator"/> face of <see cref="DirectionalMovement.MinusDi"/>.</summary>
/// <param name="period">The Wilder smoothing period.</param>
public sealed class MinusDiIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>minus-di</c>.</summary>
    public string Name => "minus-di";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period + 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) =>
        DirectionalMovement.MinusDi(bars, Period);
}
