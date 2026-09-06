using System.Globalization;

namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// Turns stored base bars into one <see cref="SessionBarOutcome"/> per trade date — complete, or absent with
/// a reason, never partial.
/// </summary>
/// <remarks>
/// <para>
/// <b>Completeness is decided by set membership, not by counting.</b> The calendar says which base buckets it
/// expected inside the session window (<see cref="BarGapDetector.ExpectedBuckets"/>); a session bar exists
/// only when the store holds every one of them. Counting would let a bucket the calendar never expected — one
/// at 16:30 Central, inside the maintenance hour, which a store can genuinely be holding (gh#412) — stand in
/// for a missing 11:00 bucket and produce a session bar that is complete by arithmetic and wrong by an hour.
/// A bar the window does not expect is ignored rather than summed.
/// </para>
/// <para>
/// The two remaining refusals are about provenance rather than coverage. A session spanning a roll splices
/// two contracts that do not trade at the same price, so its high, low and volume are a bookkeeping event
/// (ADR-0011); a session whose bars carry no contract cannot say which instrument it describes. Both are
/// complete series, and both would produce an entirely ordinary-looking bar.
/// </para>
/// <para>
/// Pure, like everything else in this assembly: no clock, no store, no configuration. The trade dates are
/// handed in, so which sessions have closed is the caller's judgement and a rebuild stays a replay
/// (ADR-0006).
/// </para>
/// </remarks>
public static class SessionBarAggregator
{
    /// <summary>
    /// Aggregates one session bar per requested trade date.
    /// </summary>
    /// <param name="bars">
    /// The stored base bars, in strictly ascending open-time order. May span trade dates. They MUST have been
    /// read at <see cref="SessionDefinition.BaseResolutionMinutes"/>: a <see cref="Bar"/> carries no
    /// resolution, so this type cannot check it and does not.
    /// </param>
    /// <param name="calendar">The session calendar deciding which buckets the venue owed.</param>
    /// <param name="definition">The session being aggregated.</param>
    /// <param name="tradeDates">The trade dates to answer for.</param>
    /// <returns>One outcome per trade date, in the order they were asked for.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The bars are not strictly ascending, the calendar carries no such session on one of the trade dates,
    /// or it expects no base bucket at all inside one of their windows.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Both refusals are <b>caller bugs</b> rather than absent bars, because an absence is a statement about
    /// a session that exists. Manufacturing one for a Saturday, or for a definition whose window holds no
    /// bucket to expect, says nothing a caller could act on — and calling the latter <c>Incomplete</c> would
    /// be worse than saying nothing, since that reason promises the missing buckets can be fetched.
    /// <see cref="SessionWindows.TradeDatesIn"/> and <see cref="SessionWindows.LastClosedTradeDates"/> only
    /// ever hand back dates that trade, and <see cref="SessionWindows.Validate"/> refuses such a definition.
    /// </para>
    /// <para>
    /// <b>The caller owns the base resolution, because nothing here can check it.</b> A <see cref="Bar"/>
    /// carries its open time and not its size, so a series read at the wrong resolution is indistinguishable
    /// from one read at the right one. Coarser than
    /// <see cref="SessionDefinition.BaseResolutionMinutes"/> is safe by accident — the expected instants are
    /// mostly not bucket starts of that series, so the outcome is <see cref="SessionBarAbsence.Incomplete"/>.
    /// <b>Finer is not.</b> A 5-minute series handed in for <c>rth</c> at a 30-minute base contains all
    /// thirteen expected instants, so it passes the completeness check and yields a bar that looks complete
    /// and is wrong: its high and low are only the thirteen 5-minute buckets that happened to start on the
    /// half hour, and its volume is a fraction of the session's. Read at
    /// <see cref="SessionDefinition.BaseResolutionMinutes"/>, which is why that number is configuration
    /// stored with the row rather than an implementation detail (ADR-0019, gh#499).
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SessionBarOutcome> Aggregate(
        IReadOnlyList<Bar> bars,
        BarSessionCalendar calendar,
        SessionDefinition definition,
        IReadOnlyList<DateOnly> tradeDates)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(tradeDates);

        IndicatorGuard.RequireStrictlyAscending(bars, nameof(bars));

        TimeSpan baseSize = TimeSpan.FromMinutes(definition.BaseResolutionMinutes);
        List<SessionBarOutcome> outcomes = new(tradeDates.Count);

        foreach (DateOnly tradeDate in tradeDates)
        {
            if (SessionWindows.WindowFor(calendar, definition, tradeDate) is not { } window)
            {
                throw new ArgumentException(
                    "The calendar carries no '" + definition.Name + "' session on "
                    + tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + ". A session bar is absent only for a session that exists; ask SessionWindows which "
                    + "trade dates the calendar carries before aggregating them.",
                    nameof(tradeDates));
            }

            outcomes.Add(AggregateOne(bars, calendar, definition, window, baseSize, tradeDate));
        }

        return outcomes;
    }

    /// <summary>The outcome for one trade date whose window is already resolved.</summary>
    /// <exception cref="ArgumentException">
    /// The calendar expects no base bucket at all inside the window — a broken definition, not an absence.
    /// </exception>
    private static SessionBarOutcome AggregateOne(
        IReadOnlyList<Bar> bars,
        BarSessionCalendar calendar,
        SessionDefinition definition,
        BarRange window,
        TimeSpan baseSize,
        DateOnly tradeDate)
    {
        HashSet<DateTimeOffset> expected = [.. BarGapDetector.ExpectedBuckets(window, baseSize, calendar)];

        // A window the calendar expects NOTHING inside is a broken definition, and it is refused on the same
        // terms as a trade date that does not trade. Reporting it as `Incomplete` would be vacuously true and
        // actively harmful: `Incomplete` promises a caller that fetching the missing buckets fixes it, and
        // there are none to fetch, so the caller would ask the venue for nothing forever. SessionWindows
        // .Validate refuses such a definition; Aggregate does not call it, so this stays reachable.
        if (expected.Count == 0)
        {
            throw new ArgumentException(
                "The calendar expects no base bucket inside the '" + definition.Name + "' window on "
                + tradeDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " ("
                + window.Start.ToString("O", CultureInfo.InvariantCulture) + " to "
                + window.End.ToString("O", CultureInfo.InvariantCulture) + " at "
                + definition.BaseResolutionMinutes.ToString(CultureInfo.InvariantCulture)
                + " minutes), so there is no session to be complete or absent. Run the definition through "
                + "SessionWindows.Validate, which refuses this one.",
                nameof(definition));
        }

        // Set membership, not a count: a bar the calendar does not expect is ignored, never summed and never
        // allowed to stand in for one that is missing.
        List<Bar> inside = [.. bars.Where(bar => expected.Contains(bar.OpenTime))];

        // `inside` cannot exceed `expected` — the bars are strictly ascending, so no two share a bucket.
        if (inside.Count != expected.Count)
        {
            return SessionBarOutcome.Absent(
                tradeDate, SessionBarAbsence.Incomplete, expected.Count, expected.Count - inside.Count);
        }

        if (ContractRollDetector.SpansRoll(inside))
        {
            return SessionBarOutcome.Absent(tradeDate, SessionBarAbsence.SpansRoll, expected.Count, 0);
        }

        // One contiguous run, so every bar carries the first one's contract.
        if (inside[0].ContractId is not { } contractId)
        {
            return SessionBarOutcome.Absent(
                tradeDate, SessionBarAbsence.ProvenanceUnknown, expected.Count, 0);
        }

        decimal high = inside[0].High;
        decimal low = inside[0].Low;
        long volume = 0;

        foreach (Bar bar in inside)
        {
            high = Math.Max(high, bar.High);
            low = Math.Min(low, bar.Low);
            volume += bar.Volume;
        }

        return SessionBarOutcome.Present(new SessionBar(
            tradeDate,
            window.Start.ToUniversalTime(),
            window.End.ToUniversalTime(),
            inside[0].Open,
            high,
            low,
            inside[^1].Close,
            volume,
            contractId,
            expected.Count));
    }
}
