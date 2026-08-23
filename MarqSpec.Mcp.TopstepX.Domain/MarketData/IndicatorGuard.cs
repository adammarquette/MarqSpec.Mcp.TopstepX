namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The preconditions every indicator shares.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is checked rather than assumed because a shuffled series does not fail — it quietly computes a
/// different, wrong number. True range, Wilder smoothing and every moving average are path-dependent, so
/// "the bars arrived in whatever order the query returned them" is a silent-corruption bug, not a style issue.
/// </para>
/// <para>
/// A contract splice is the same failure wearing different clothes, and it is checked here for the same
/// reason: a series carrying two contracts computes a perfectly ordinary-looking number out of a roll gap.
/// Both checks live on the shared path so a new indicator inherits them rather than remembering them.
/// </para>
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
    /// Throws unless every bar in the series came from the same contract (ADR-0011).
    /// </summary>
    /// <param name="bars">The series.</param>
    /// <param name="parameterName">The caller's parameter name, for the exception.</param>
    /// <exception cref="ArgumentException">The series crosses a contract boundary.</exception>
    /// <remarks>
    /// <para>
    /// A refusal rather than a null run, because the caller is the one that can fix it: an indicator handed a
    /// spliced series has no honest value to return for <i>any</i> bar, not merely the ones near the seam —
    /// Wilder smoothing carries the roll gap forward indefinitely. The fix is to segment the series first
    /// (<see cref="ContractRollDetector.Segment"/>) and compute each run on its own.
    /// </para>
    /// <para>
    /// A series whose bars <b>all</b> carry no contract id is a single run of unknown provenance and passes.
    /// Every row written before the provenance was stored is in that state, and refusing them would take a
    /// working store offline to punish it for a gap it cannot close. What does not pass is an unrecorded run
    /// beside a recorded one: those are not known to be the same contract, and assuming they are is the
    /// assumption this check exists to refuse.
    /// </para>
    /// </remarks>
    public static void RequireSingleContract(IReadOnlyList<Bar> bars, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(bars);

        IReadOnlyList<ContractSegment> segments = ContractRollDetector.Segment(bars);
        if (segments.Count <= 1)
        {
            return;
        }

        ContractSegment previous = segments[0];
        ContractSegment next = segments[1];

        throw new ArgumentException(
            "The bars span a contract roll and no value can be computed across one: "
            + Describe(previous) + " runs to "
            + previous.LastBucket.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + ", then " + Describe(next) + " begins at "
            + next.FirstBucket.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + ". Two contracts do not trade at the same price, so a value smoothed across the seam reports "
            + "the roll gap as market movement. Segment the series by contract and compute each run "
            + "separately.",
            parameterName);
    }

    private static string Describe(ContractSegment segment) =>
        segment.ContractId is null ? "an unrecorded contract" : "contract '" + segment.ContractId + "'";

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
