namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The holes in a half-open window once the ranges that were actually covered are taken out.
/// </summary>
/// <remarks>
/// <para>
/// The same convention as <see cref="BarRange"/>: <c>[Start, End)</c>. Adjacent ranges that meet
/// — one's end equal to the next's start — are one stretch, not a one-instant hole. A hole of
/// one instant is a real gap; inventing one where two ranges touch would page a monitor for a
/// recorder that never stopped.
/// </para>
/// <para>
/// Nothing here reads a clock or a store. The caller decides which window and which ranges count
/// (ADR-0006). A range that falls outside the window is ignored. An empty or inverted window has
/// no gaps: there is no elapsed time to be missing.
/// </para>
/// </remarks>
public static class SessionCoverageGaps
{
    /// <summary>One half-open interval <c>[Start, End)</c>.</summary>
    /// <param name="Start">Inclusive.</param>
    /// <param name="End">Exclusive.</param>
    public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End);

    /// <summary>
    /// The uncovered stretches of <c>[windowStart, windowEnd)</c>, ascending.
    /// </summary>
    /// <param name="windowStart">The start of the window, inclusive.</param>
    /// <param name="windowEnd">The end of the window, exclusive.</param>
    /// <param name="covered">The ranges that count as covered. May extend outside the window.</param>
    /// <returns>The gaps. Empty when the window is empty or fully covered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="covered"/> is null.</exception>
    public static IReadOnlyList<Interval> Find(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<Interval> covered)
    {
        ArgumentNullException.ThrowIfNull(covered);

        if (windowEnd <= windowStart)
        {
            return [];
        }

        List<Interval> clipped = [];
        foreach (Interval range in covered)
        {
            DateTimeOffset start = range.Start < windowStart ? windowStart : range.Start;
            DateTimeOffset end = range.End > windowEnd ? windowEnd : range.End;
            if (end > start)
            {
                clipped.Add(new Interval(start, end));
            }
        }

        clipped.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        List<Interval> merged = [];
        foreach (Interval range in clipped)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            Interval last = merged[^1];
            if (range.Start <= last.End)
            {
                DateTimeOffset end = range.End > last.End ? range.End : last.End;
                merged[^1] = new Interval(last.Start, end);
                continue;
            }

            merged.Add(range);
        }

        List<Interval> gaps = [];
        DateTimeOffset cursor = windowStart;
        foreach (Interval range in merged)
        {
            if (range.Start > cursor)
            {
                gaps.Add(new Interval(cursor, range.Start));
            }

            if (range.End > cursor)
            {
                cursor = range.End;
            }
        }

        if (cursor < windowEnd)
        {
            gaps.Add(new Interval(cursor, windowEnd));
        }

        return gaps;
    }
}
