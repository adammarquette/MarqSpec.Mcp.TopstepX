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
    /// <param name="bars">The stored base bars, in strictly ascending open-time order. May span trade dates.</param>
    /// <param name="calendar">The session calendar deciding which buckets the venue owed.</param>
    /// <param name="definition">The session being aggregated.</param>
    /// <param name="tradeDates">The trade dates to answer for.</param>
    /// <returns>One outcome per trade date, in the order they were asked for.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The bars are not strictly ascending, or the calendar carries no such session on one of the trade dates.
    /// </exception>
    /// <remarks>
    /// A trade date the calendar does not trade is a <b>caller bug</b>, not an absent bar: an absence is a
    /// statement about a session that exists, and manufacturing one for a Saturday would say nothing a caller
    /// could act on. <see cref="SessionWindows.TradeDatesIn"/> and
    /// <see cref="SessionWindows.LastClosedTradeDates"/> only ever hand back dates that trade.
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

            outcomes.Add(AggregateOne(bars, calendar, window, baseSize, tradeDate));
        }

        return outcomes;
    }

    /// <summary>The outcome for one trade date whose window is already resolved.</summary>
    private static SessionBarOutcome AggregateOne(
        IReadOnlyList<Bar> bars,
        BarSessionCalendar calendar,
        BarRange window,
        TimeSpan baseSize,
        DateOnly tradeDate)
    {
        HashSet<DateTimeOffset> expected = [.. BarGapDetector.ExpectedBuckets(window, baseSize, calendar)];

        // Set membership, not a count: a bar the calendar does not expect is ignored, never summed and never
        // allowed to stand in for one that is missing.
        List<Bar> inside = [.. bars.Where(bar => expected.Contains(bar.OpenTime))];

        // `inside` cannot exceed `expected` — the bars are strictly ascending, so no two share a bucket. The
        // zero-expected case falls in here too: a definition the calendar leaves no room for is an absence,
        // not a bar built from nothing.
        if (expected.Count == 0 || inside.Count != expected.Count)
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
