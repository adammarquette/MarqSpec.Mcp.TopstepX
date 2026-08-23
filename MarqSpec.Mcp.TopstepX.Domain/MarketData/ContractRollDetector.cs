namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// One contiguous run of bars produced by a single venue contract.
/// </summary>
/// <param name="ContractId">
/// The contract, or <see langword="null"/> when the run's provenance was never recorded. Null is a value of
/// its own, not a wildcard — an unrecorded run beside a known one is a seam, because the two are not
/// <i>known</i> to be the same contract.
/// </param>
/// <param name="StartIndex">The index of the run's first bar in the series it was segmented from.</param>
/// <param name="BarCount">How many bars are in the run.</param>
/// <param name="FirstBucket">When the run's first bar opened.</param>
/// <param name="LastBucket">When the run's last bar opened.</param>
public sealed record ContractSegment(
    string? ContractId,
    int StartIndex,
    int BarCount,
    DateTimeOffset FirstBucket,
    DateTimeOffset LastBucket);

/// <summary>
/// Finds the contract seams inside a symbol-keyed bar series (ADR-0011).
/// </summary>
/// <remarks>
/// <para>
/// Bars are stored under the venue-neutral symbol (<c>ES</c>) and contract resolution picks the front month.
/// Every quarter the front month rolls, and the next fetch writes the new contract's bars under the same key,
/// directly beside the old one's. <b>Nothing in the bucket sequence marks that</b>: the timestamps are
/// contiguous and the series looks continuous.
/// </para>
/// <para>
/// It is not continuous. The roll gap between adjacent ES quarters is routinely tens of points, and anything
/// computed across the seam inherits it — ATR reads the gap as volatility, key levels form at prices the
/// contract in front has never traded at, and every smoothed indicator carries the jump forward. None of it
/// errors, which is the whole problem.
/// </para>
/// <para>
/// Pure, like everything else in this assembly: the segmentation is a function of the bars handed in, so a
/// recomputation over the same stored series produces the same seams and "rebuild = replay" survives
/// (ADR-0006).
/// </para>
/// </remarks>
public static class ContractRollDetector
{
    /// <summary>
    /// Splits a series into contiguous runs of bars sharing a contract.
    /// </summary>
    /// <param name="bars">The series, in ascending time order.</param>
    /// <returns>The runs, in series order. Empty for an empty series.</returns>
    /// <remarks>
    /// <b>Contiguous runs, not distinct contracts.</b> A contract that reappears after another one is
    /// reported as a third run rather than folded back into the first: interleaving is what a backfill under
    /// the wrong front month looks like, and it is worse than a clean splice, not better. Folding it away
    /// would hide exactly the disorder worth seeing.
    /// </remarks>
    public static IReadOnlyList<ContractSegment> Segment(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (bars.Count == 0)
        {
            return [];
        }

        List<ContractSegment> segments = [];
        int start = 0;

        for (int i = 1; i <= bars.Count; i++)
        {
            bool boundary = i == bars.Count
                || !string.Equals(bars[i].ContractId, bars[start].ContractId, StringComparison.Ordinal);

            if (!boundary)
            {
                continue;
            }

            segments.Add(new ContractSegment(
                bars[start].ContractId,
                start,
                i - start,
                bars[start].OpenTime,
                bars[i - 1].OpenTime));

            start = i;
        }

        return segments;
    }

    /// <summary>
    /// Whether a series crosses a contract boundary.
    /// </summary>
    /// <param name="bars">The series, in ascending time order.</param>
    /// <returns><see langword="true"/> when the bars came from more than one run.</returns>
    public static bool SpansRoll(IReadOnlyList<Bar> bars) => Segment(bars).Count > 1;

    /// <summary>
    /// Returns the most recent contiguous run — the bars belonging to the contract in front.
    /// </summary>
    /// <param name="bars">The series, in ascending time order.</param>
    /// <returns>
    /// The trailing run, or the whole series when it carries only one. Empty for an empty series.
    /// </returns>
    /// <remarks>
    /// This is what anything <i>detected</i> over a series must be confined to. A support level built from
    /// bars of the expiring contract sits at a price the contract in front has never traded, and an agent
    /// reading it has no way to tell that from a level it is about to touch.
    /// </remarks>
    public static IReadOnlyList<Bar> Newest(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        IReadOnlyList<ContractSegment> segments = Segment(bars);
        if (segments.Count <= 1)
        {
            return bars;
        }

        ContractSegment last = segments[^1];
        return [.. bars.Skip(last.StartIndex).Take(last.BarCount)];
    }
}
