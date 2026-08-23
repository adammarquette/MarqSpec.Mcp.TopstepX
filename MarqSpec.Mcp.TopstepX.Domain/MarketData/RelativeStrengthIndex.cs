namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Relative Strength Index, Wilder's smoothing.
/// </summary>
public static class RelativeStrengthIndex
{
    /// <summary>
    /// Computes RSI for each bar, aligned one-to-one with <paramref name="bars"/>.
    /// </summary>
    /// <param name="bars">The series, in strictly ascending time order.</param>
    /// <param name="period">The smoothing period. Wilder's default is 14.</param>
    /// <returns>
    /// One entry per bar in <c>[0, 100]</c>, <see langword="null"/> until index <paramref name="period"/>.
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

        decimal gainTotal = 0m;
        decimal lossTotal = 0m;
        for (int i = 1; i <= period; i++)
        {
            decimal change = bars[i].Close - bars[i - 1].Close;
            if (change > 0m)
            {
                gainTotal += change;
            }
            else
            {
                lossTotal -= change; // change is <= 0, so this accumulates a positive magnitude.
            }
        }

        // Seed from the TOTALS rather than the averages. RSI is 100 * avgGain / (avgGain + avgLoss), and the
        // /period in each average cancels — so working from totals gives the identical result without the
        // non-terminating decimal division (5/3 and friends) that would otherwise drift the seed.
        values[period] = Rsi(gainTotal, lossTotal);

        decimal averageGain = gainTotal / period;
        decimal averageLoss = lossTotal / period;

        for (int i = period + 1; i < bars.Count; i++)
        {
            decimal change = bars[i].Close - bars[i - 1].Close;
            decimal gain = change > 0m ? change : 0m;
            decimal loss = change < 0m ? -change : 0m;

            averageGain = ((averageGain * (period - 1)) + gain) / period;
            averageLoss = ((averageLoss * (period - 1)) + loss) / period;
            values[i] = Rsi(averageGain, averageLoss);
        }

        return values;
    }

    private static decimal Rsi(decimal gain, decimal loss)
    {
        // A window with no movement at all is neutral. Returning 0 or 100 here — which the general formula
        // would do by accident — would read as a maximal signal from a flat market.
        if (gain == 0m && loss == 0m)
        {
            return 50m;
        }

        // Only gains: the textbook form divides by zero here, so short-circuit rather than guard downstream.
        if (loss == 0m)
        {
            return 100m;
        }

        return 100m * gain / (gain + loss);
    }
}

/// <summary>The <see cref="IIndicator"/> face of <see cref="RelativeStrengthIndex"/>.</summary>
/// <param name="period">The smoothing period.</param>
public sealed class RsiIndicator(int period) : IIndicator
{
    /// <summary>The stored name, <c>rsi</c>.</summary>
    public string Name => "rsi";

    /// <inheritdoc />
    public int Period { get; } = period;

    /// <inheritdoc />
    public int WarmupBars => Period + 1;

    /// <inheritdoc />
    public IReadOnlyList<decimal?> Compute(IReadOnlyList<Bar> bars) => RelativeStrengthIndex.Compute(bars, Period);
}
