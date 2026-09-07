using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// One piece of an outstanding range, and the contracts it is to be fetched from.
/// </summary>
/// <param name="Range">The piece, half-open as every range in this system is.</param>
/// <param name="Candidates">
/// The contract ids to ask, nearest expiry first. Exactly one for a present slice; one or more for a
/// historical one, whose winner is decided from the bars afterwards by <c>HistoricalContractPolicy</c>.
/// </param>
/// <param name="Present">
/// Whether this piece sits in the present band — the stretch the venue's own active contract answers for
/// (ADR-0020 §1). A present slice is fetched exactly as it is today; a historical one is not.
/// </param>
public sealed record RangeSlice(BarRange Range, IReadOnlyList<string> Candidates, bool Present);

/// <summary>
/// Cuts the ranges a read still owes the venue into the present band and the historical bands, and names
/// what each is to be fetched from (ADR-0020 §1–2, gh#505).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and deliberately container-free.</b> It reaches no store, no venue and no clock: the tenure start
/// is handed in as a value, and every candidate id arrives through a lookup the caller has already resolved.
/// That is what makes the cutting testable without a database, and it is the same discipline
/// <c>HistoricalContractPolicy</c> keeps one layer down (ADR-0006).
/// </para>
/// <para>
/// It lives in the host rather than in <c>Domain</c> because it is a fact about <i>this fetch flow</i> —
/// which band the venue's pick owns — rather than about the market.
/// </para>
/// </remarks>
public static class HistoricalRangePlanner
{
    /// <summary>
    /// Cuts each outstanding range at the tenure start, and each historical piece wherever the trade date's
    /// candidate set changes.
    /// </summary>
    /// <param name="outstanding">The ranges the read still owes, ascending and non-overlapping.</param>
    /// <param name="tenureStart">
    /// The first bucket of the store's trailing run of the venue front, <c>T(F)</c>. At or after it is the
    /// present band; before it is history.
    /// </param>
    /// <param name="frontContractId">The contract the venue marks active.</param>
    /// <param name="calendar">The session calendar deciding which trade date a bucket belongs to.</param>
    /// <param name="cycle">The product's contract month cycle.</param>
    /// <param name="depth">How many listed expiries are candidates for one historical trade date.</param>
    /// <param name="resolveContractId">
    /// The venue contract id for a constructed expiry, or <see langword="null"/> when the venue does not list
    /// it. Synchronous by design: the caller existence-checks every candidate before planning, so the planner
    /// never reaches the venue.
    /// </param>
    /// <returns>The slices, ascending, non-overlapping, and covering the input exactly.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is below one.</exception>
    public static IReadOnlyList<RangeSlice> PlanSlices(
        IReadOnlyList<BarRange> outstanding,
        DateTimeOffset tenureStart,
        string frontContractId,
        BarSessionCalendar calendar,
        ContractMonthCycle cycle,
        int depth,
        Func<ContractExpiry, string?> resolveContractId)
    {
        ArgumentNullException.ThrowIfNull(outstanding);
        ArgumentException.ThrowIfNullOrWhiteSpace(frontContractId);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(resolveContractId);
        if (depth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "At least one candidate is needed.");
        }

        List<RangeSlice> slices = [];

        foreach (BarRange range in outstanding)
        {
            if (range is null || range.IsEmpty)
            {
                continue;
            }

            // History first, then the present: the two pieces of one range are already in ascending order
            // that way, and the input is ascending, so the whole answer is.
            if (range.Start < tenureStart)
            {
                DateTimeOffset end = range.End <= tenureStart ? range.End : tenureStart;
                for (DateTimeOffset from = range.Start; from < end;)
                {
                    ContractExpiry[] expiries =
                        [.. cycle.CandidatesFor(TradeDateOf(calendar, from), depth)];
                    DateTimeOffset next = NextCandidateChange(from, end, calendar, cycle, depth, expiries[0]);

                    // No listed candidate left is a DEGRADATION, not an empty answer: the slice is fetched
                    // from the venue's own pick, which is today's behaviour, and the caller logs the warning
                    // and withholds the permanent memo. A slice with nothing to ask would report a quiet
                    // market instead -- the plausible-looking absence this server exists to refuse.
                    IReadOnlyList<string> candidates = Listed(expiries, resolveContractId);
                    slices.Add(new RangeSlice(
                        new BarRange(from, next),
                        candidates.Count > 0 ? candidates : [frontContractId],
                        Present: false));

                    from = next;
                }
            }

            if (range.End > tenureStart)
            {
                DateTimeOffset start = range.Start >= tenureStart ? range.Start : tenureStart;
                slices.Add(new RangeSlice(
                    new BarRange(start, range.End), [frontContractId], Present: true));
            }
        }

        return slices;
    }

    /// <summary>
    /// The trade date a bucket belongs to, falling back to its UTC date for a bucket the calendar places
    /// outside every session — exactly as <c>HistoricalContractPolicy</c> groups bars.
    /// </summary>
    /// <param name="calendar">The session calendar.</param>
    /// <param name="instant">The instant.</param>
    /// <returns>The trade date.</returns>
    private static DateOnly TradeDateOf(BarSessionCalendar calendar, DateTimeOffset instant) =>
        calendar.TradeDateFor(instant) ?? DateOnly.FromDateTime(instant.UtcDateTime);

    /// <summary>
    /// The first instant in <c>(from, end)</c> whose trade date names a different candidate set, or
    /// <paramref name="end"/> when the set holds for the whole stretch.
    /// </summary>
    /// <param name="from">The start of the stretch, whose candidate set is the one being left.</param>
    /// <param name="end">The end of the stretch, exclusive.</param>
    /// <param name="calendar">The session calendar.</param>
    /// <param name="cycle">The product's contract month cycle.</param>
    /// <param name="depth">How many listed expiries are candidates.</param>
    /// <param name="nearest">The nearest candidate at <paramref name="from"/>.</param>
    /// <returns>The change instant, or <paramref name="end"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>A binary search rather than a scan, and it is exact.</b> The candidate set is a function of the
    /// trade date alone, the trade date is non-decreasing in the instant, and
    /// <c>ContractMonthCycle.CandidatesFor</c> only ever moves the nearest expiry <i>forward</i> — so
    /// "the set has changed" is false then true across the stretch and never returns. A step scan would have
    /// to pick a granularity, and would land the cut at a step boundary rather than at the session open where
    /// the trade date actually turns.
    /// </para>
    /// <para>
    /// The tie-break is <c>Rank</c> rather than set equality: a month change that does not move the nearest
    /// expiry (April to May on a quarterly cycle) is not a boundary, and splitting there would produce two
    /// adjacent slices asking the same contracts the same question.
    /// </para>
    /// </remarks>
    private static DateTimeOffset NextCandidateChange(
        DateTimeOffset from,
        DateTimeOffset end,
        BarSessionCalendar calendar,
        ContractMonthCycle cycle,
        int depth,
        ContractExpiry nearest)
    {
        long lo = from.UtcTicks;
        long hi = end.UtcTicks;

        if (!HasChanged(hi - 1))
        {
            return end;
        }

        while (hi - lo > 1)
        {
            long mid = lo + ((hi - lo) / 2);
            if (HasChanged(mid))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return new DateTimeOffset(hi, TimeSpan.Zero);

        bool HasChanged(long ticks) =>
            cycle.CandidatesFor(
                TradeDateOf(calendar, new DateTimeOffset(ticks, TimeSpan.Zero)), depth)[0].Rank > nearest.Rank;
    }

    /// <summary>The venue ids for a candidate set, unlisted expiries dropped.</summary>
    /// <param name="expiries">The candidates, nearest first.</param>
    /// <param name="resolveContractId">The expiry-to-id lookup.</param>
    /// <returns>The ids, nearest expiry first.</returns>
    private static IReadOnlyList<string> Listed(
        IReadOnlyList<ContractExpiry> expiries,
        Func<ContractExpiry, string?> resolveContractId)
    {
        List<string> ids = [];
        foreach (ContractExpiry expiry in expiries)
        {
            if (resolveContractId(expiry) is { } contractId && !ids.Contains(contractId, StringComparer.Ordinal))
            {
                ids.Add(contractId);
            }
        }

        return ids;
    }
}
