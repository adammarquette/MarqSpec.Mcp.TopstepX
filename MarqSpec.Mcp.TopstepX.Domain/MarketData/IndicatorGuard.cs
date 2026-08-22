namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The precondition every indicator shares.
/// </summary>
/// <remarks>
/// Ordering is checked rather than assumed because a shuffled series does not fail — it quietly computes a
/// different, wrong number. True range, Wilder smoothing and every moving average are path-dependent, so
/// "the bars arrived in whatever order the query returned them" is a silent-corruption bug, not a style issue.
/// </remarks>
public static class IndicatorGuard
{
    /// <summary>
    /// Throws unless the bars are in strictly ascending open-time order.
    /// </summary>
    /// <param name="bars">The series.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
    /// <exception cref="ArgumentException">
    /// Two bars share an open time, or a bar opens before the one preceding it.
    /// </exception>
    public static void RequireStrictlyAscending(IReadOnlyList<Bar> bars, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(bars);

        for (int i = 1; i < bars.Count; i++)
        {
            if (bars[i].OpenTime <= bars[i - 1].OpenTime)
            {
                throw new ArgumentException(
                    "Bars must be in strictly ascending time order; bar "
                    + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " opens at " + bars[i].OpenTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    + ", not after " + bars[i - 1].OpenTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    + ".",
                    parameterName);
            }
        }
    }

    /// <summary>
    /// Throws unless a period is at least one.
    /// </summary>
    /// <param name="period">The period.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">The period is less than one.</exception>
    public static void RequirePositivePeriod(int period, string parameterName)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(parameterName, period, "A period must be at least 1.");
        }
    }
}
