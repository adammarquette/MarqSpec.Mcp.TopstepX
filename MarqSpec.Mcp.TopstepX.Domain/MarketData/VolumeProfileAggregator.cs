namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Aggregates <see cref="FootprintCell"/>s into a volume profile: volume by price, the point of
/// control, and the 70% value area (gh#221).
/// </summary>
/// <remarks>
/// <para>
/// Pure, like everything else in this assembly: the profile is a function of the cells and the
/// listening ranges handed in. No clock, no store, no config — that is what makes a rebuild a
/// replay (ADR-0006).
/// </para>
/// <para>
/// <b>The point of control</b> is the price with the most volume (buy plus sell). A tie goes to
/// the price closest to the midpoint of the lowest and highest prices that traded. A remaining
/// tie goes to the lower price, so two implementations handed the same cells in opposite order
/// cannot disagree.
/// </para>
/// <para>
/// <b>The value area</b> is the conventional 70% Market Profile expansion. Start at the POC.
/// While the area holds less than seven tenths of total volume, compare the next <b>two</b>
/// unused prices above it with the next two below it. A side with only one unused price
/// contributes that one — the even/odd expansion-boundary. Add the side with more volume; a
/// volume tie adds the lower-price side. The whole winning group is added, even when that
/// crosses 70%. Stopping at the single closer price would be a different definition.
/// </para>
/// <para>
/// <b>A window that spans a roll is confined to the contract in front</b> (ADR-0011). Two
/// contracts do not trade at the same price, so a spliced profile puts the POC at a level the
/// front month never traded. The narrowing is reported rather than silently applied — the same
/// cut <c>get_key_levels</c> makes with <c>detectedOverBars</c>. An advisory flag beside a
/// spliced number is still a wrong number.
/// </para>
/// <para>
/// <b>The reported window comes from the listening ranges, not the ask.</b> The tape has a
/// beginning and can have holes, and neither is recoverable. A window with no overlapping
/// coverage refuses rather than returning an empty profile.
/// </para>
/// </remarks>
public static class VolumeProfileAggregator
{
    /// <summary>The conventional value-area fraction: seven tenths of total volume.</summary>
    public const decimal ValueAreaFraction = 0.70m;

    /// <summary>
    /// Builds a profile from a fixed cell set.
    /// </summary>
    /// <param name="cells">The cells. Order does not matter; volume is commutative.</param>
    /// <returns>The profile. Prices that did not trade are absent, not zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The cells mix instruments or resolutions, or they carry no volume.
    /// </exception>
    public static VolumeProfile From(IReadOnlyList<FootprintCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count == 0)
        {
            throw new ArgumentException(
                "A volume profile needs volume; an empty cell set has none.",
                nameof(cells));
        }

        string instrument = cells[0].Instrument;
        int resolutionMinutes = cells[0].ResolutionMinutes;
        Dictionary<decimal, long> byPrice = [];

        foreach (FootprintCell cell in cells)
        {
            if (!string.Equals(cell.Instrument, instrument, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A volume profile is one instrument; these cells mix instruments.",
                    nameof(cells));
            }

            if (cell.ResolutionMinutes != resolutionMinutes)
            {
                throw new ArgumentException(
                    "A volume profile is one resolution; these cells mix bar sizes.",
                    nameof(cells));
            }

            long volume = cell.BuyVolume + cell.SellVolume;
            if (volume <= 0)
            {
                continue;
            }

            byPrice[cell.Price] = byPrice.GetValueOrDefault(cell.Price) + volume;
        }

        if (byPrice.Count == 0)
        {
            throw new ArgumentException(
                "A volume profile needs volume; these cells carry none.",
                nameof(cells));
        }

        List<VolumeAtPrice> levels = [.. byPrice
            .Select(pair => new VolumeAtPrice(pair.Key, pair.Value))
            .OrderBy(level => level.Price)];

        long total = 0;
        foreach (VolumeAtPrice level in levels)
        {
            total += level.Volume;
        }

        decimal midpoint = (levels[0].Price + levels[^1].Price) / 2m;
        int pocIndex = 0;

        for (int i = 1; i < levels.Count; i++)
        {
            if (IsBetterPoc(levels[i], levels[pocIndex], midpoint))
            {
                pocIndex = i;
            }
        }

        int low = pocIndex;
        int high = pocIndex;
        long valueAreaVolume = levels[pocIndex].Volume;
        decimal target = total * ValueAreaFraction;

        while (valueAreaVolume < target && (low > 0 || high < levels.Count - 1))
        {
            int belowCount = Math.Min(2, low);
            int aboveCount = Math.Min(2, levels.Count - 1 - high);
            long belowVolume = SumSide(levels, low, -1, belowCount);
            long aboveVolume = SumSide(levels, high, 1, aboveCount);

            bool addBelow = belowCount > 0
                && (aboveCount == 0 || belowVolume >= aboveVolume);

            if (addBelow)
            {
                low -= belowCount;
                valueAreaVolume += belowVolume;
            }
            else
            {
                high += aboveCount;
                valueAreaVolume += aboveVolume;
            }
        }

        return new VolumeProfile(
            levels,
            levels[pocIndex].Price,
            levels[low].Price,
            levels[high].Price,
            valueAreaVolume,
            total);
    }

    /// <summary>
    /// Confines a requested window to the contract in front that was actually listened to.
    /// </summary>
    /// <param name="requestedStart">The start of the ask, inclusive.</param>
    /// <param name="requestedEnd">The end of the ask, exclusive.</param>
    /// <param name="coverage">Listening ranges. Values, not store rows.</param>
    /// <returns>
    /// The covered window. <see cref="CoveredTapeWindow.Start"/> and
    /// <see cref="CoveredTapeWindow.End"/> come from the ledger intersected with the ask,
    /// never from the ask alone.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="coverage"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ask is empty or inverted.</exception>
    /// <exception cref="InvalidOperationException">
    /// No listening range overlaps the ask — a window with no tape.
    /// </exception>
    public static CoveredTapeWindow Confine(
        DateTimeOffset requestedStart,
        DateTimeOffset requestedEnd,
        IReadOnlyList<ListeningRange> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        if (requestedEnd <= requestedStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedEnd),
                requestedEnd,
                "A profile window must end after it starts.");
        }

        List<ListeningRange> overlapping = [];

        foreach (ListeningRange range in coverage)
        {
            DateTimeOffset start = range.RangeStart > requestedStart ? range.RangeStart : requestedStart;
            DateTimeOffset end = range.RangeEnd < requestedEnd ? range.RangeEnd : requestedEnd;

            if (end > start)
            {
                overlapping.Add(new ListeningRange(range.ContractId, start, end));
            }
        }

        if (overlapping.Count == 0)
        {
            throw new InvalidOperationException(
                "A window with no tape cannot produce a volume profile. There is no market-tape "
                + "backfill, so an empty profile would look like a quiet market.");
        }

        string front = FrontContract(overlapping);
        List<ListeningRange> confined = [];

        foreach (ListeningRange range in overlapping)
        {
            if (string.Equals(range.ContractId, front, StringComparison.Ordinal))
            {
                confined.Add(range);
            }
        }

        DateTimeOffset coveredStart = confined[0].RangeStart;
        DateTimeOffset coveredEnd = confined[0].RangeEnd;

        foreach (ListeningRange range in confined)
        {
            if (range.RangeStart < coveredStart)
            {
                coveredStart = range.RangeStart;
            }

            if (range.RangeEnd > coveredEnd)
            {
                coveredEnd = range.RangeEnd;
            }
        }

        bool narrowed = confined.Count != overlapping.Count
            || coveredStart > requestedStart
            || coveredEnd < requestedEnd;

        return new CoveredTapeWindow(front, coveredStart, coveredEnd, narrowed);
    }

    /// <summary>
    /// Whether a bar <c>[bucketStart, bucketStart + resolution)</c> overlaps
    /// <c>[windowStart, windowEnd)</c>.
    /// </summary>
    public static bool BarOverlapsWindow(
        DateTimeOffset bucketStart,
        int resolutionMinutes,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd) =>
        bucketStart < windowEnd && bucketStart.AddMinutes(resolutionMinutes) > windowStart;

    /// <summary>
    /// The contract whose listening continues latest — the one in front after a roll.
    /// </summary>
    private static string FrontContract(IReadOnlyList<ListeningRange> overlapping)
    {
        string front = overlapping[0].ContractId;
        DateTimeOffset latestEnd = overlapping[0].RangeEnd;
        DateTimeOffset latestStart = overlapping[0].RangeStart;

        for (int i = 1; i < overlapping.Count; i++)
        {
            ListeningRange range = overlapping[i];
            int byEnd = range.RangeEnd.CompareTo(latestEnd);

            if (byEnd > 0
                || (byEnd == 0 && range.RangeStart > latestStart)
                || (byEnd == 0
                    && range.RangeStart == latestStart
                    && string.CompareOrdinal(range.ContractId, front) < 0))
            {
                front = range.ContractId;
                latestEnd = range.RangeEnd;
                latestStart = range.RangeStart;
            }
        }

        return front;
    }

    private static bool IsBetterPoc(VolumeAtPrice candidate, VolumeAtPrice current, decimal midpoint)
    {
        if (candidate.Volume != current.Volume)
        {
            return candidate.Volume > current.Volume;
        }

        decimal candidateDistance = DistanceFrom(candidate.Price, midpoint);
        decimal currentDistance = DistanceFrom(current.Price, midpoint);

        if (candidateDistance != currentDistance)
        {
            return candidateDistance < currentDistance;
        }

        return candidate.Price < current.Price;
    }

    private static decimal DistanceFrom(decimal price, decimal midpoint) =>
        price >= midpoint ? price - midpoint : midpoint - price;

    private static long SumSide(
        IReadOnlyList<VolumeAtPrice> levels,
        int from,
        int step,
        int count)
    {
        long volume = 0;

        for (int i = 1; i <= count; i++)
        {
            volume += levels[from + (step * i)].Volume;
        }

        return volume;
    }
}
