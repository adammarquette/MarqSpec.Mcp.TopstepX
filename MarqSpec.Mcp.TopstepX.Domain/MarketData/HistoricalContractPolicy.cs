namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The bars one trade date keeps, and whose they are, once several contracts have answered for it.
/// </summary>
/// <param name="TradeDate">
/// The trade date <see cref="BarSessionCalendar.TradeDateFor"/> assigned — or, for a bar the calendar places
/// outside every session, the bar's UTC date.
/// </param>
/// <param name="ContractId">The contract whose bars the trade date keeps.</param>
/// <param name="Bars">That contract's bars for the trade date, ascending.</param>
/// <param name="VolumeByContract">
/// The summed volume of every contract that answered bars for the trade date, so a caller can see how
/// decisive the choice was. A contract that answered nothing for the date is absent.
/// </param>
public sealed record TradeDateSelection(
    DateOnly TradeDate,
    string ContractId,
    IReadOnlyList<Bar> Bars,
    IReadOnlyDictionary<string, long> VolumeByContract);

/// <summary>
/// Decides, per trade date, which candidate contract's bars belong to the history (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// A historical range fetched from the contract the venue marks active <i>today</i> stores the thin series:
/// on 2026-05-04 the expired <c>MES.M26</c> carried 1 380 105 contracts and the then-listed <c>U26</c>
/// carried 2 854, and nothing errors when the 2 854 are stored (gh#494). Bar volume names the changeover
/// cleanly — the front is the contract with the most volume, the principle gh#219 settled for the tape —
/// so the history is taken from the volume winner, decided per <b>trade date</b> so a roll leaves one seam
/// where the market had one and never an interleaving.
/// </para>
/// <para>
/// Pure, like everything else in this assembly: the selection is a function of the bars, the calendar and
/// the store's existing attributions handed in. No clock, no store, no gateway — the same fixtures decide
/// the same way forever (ADR-0006).
/// </para>
/// <para>
/// <b>A trade date the store already holds keeps its contract.</b> The read path never rewrites an attributed
/// bucket; that is what keeps a warm read byte-identical to the one before it, and what keeps a seam from
/// appearing inside a day the store already answered for. The caller says which trade dates those are, and
/// passes none of them when it is <i>meant</i> to rewrite — the reselect verb. A stored contract that
/// answered no bars for the date cannot be kept, and the volume decides instead.
/// </para>
/// <para>
/// <b>A tie goes to the nearer expiry.</b> The tape's front-month rule refuses a tie, because it is naming a
/// fact and a tie is no fact. This policy has to choose — a trade date's bars have to come from somewhere —
/// and the nearer expiry is the deterministic choice that agrees with the venue's own ordering. An id whose
/// expiry cannot be read sorts after every one whose can, and ties among those on the id itself, so the
/// answer never depends on the order the candidates arrived in.
/// </para>
/// <para>
/// <b>A bar outside every session groups under its UTC date.</b> The calendar assigns no trade date to a
/// maintenance-window, weekend or holiday bucket, but the venue published the bar and the policy is a
/// grouping, not a filter. Its UTC date is a label for the group, not a session — the caller stores those
/// bars like any other and the calendar goes on saying nothing is expected there.
/// </para>
/// </remarks>
public static class HistoricalContractPolicy
{
    /// <summary>
    /// Groups every candidate's bars by trade date and names the contract each date keeps.
    /// </summary>
    /// <param name="barsByContract">Each candidate contract's bars, keyed by contract id. Order does not matter.</param>
    /// <param name="calendar">The session calendar that assigns each bar a trade date.</param>
    /// <param name="storedContractByTradeDate">
    /// The contract the store already holds attributed bars for, per trade date. Empty when nothing is to
    /// be kept.
    /// </param>
    /// <returns>One selection per trade date that has bars, ascending. Empty when no candidate answered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A bar under one contract's key carries another contract's id.
    /// </exception>
    public static IReadOnlyList<TradeDateSelection> Decide(
        IReadOnlyDictionary<string, IReadOnlyList<Bar>> barsByContract,
        BarSessionCalendar calendar,
        IReadOnlyDictionary<DateOnly, string> storedContractByTradeDate)
    {
        ArgumentNullException.ThrowIfNull(barsByContract);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(storedContractByTradeDate);

        SortedDictionary<DateOnly, Dictionary<string, List<Bar>>> byTradeDate = [];

        foreach ((string contractId, IReadOnlyList<Bar> bars) in barsByContract)
        {
            ArgumentNullException.ThrowIfNull(bars, nameof(barsByContract));

            foreach (Bar bar in bars)
            {
                if (bar.ContractId is not null && !string.Equals(bar.ContractId, contractId, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "A bar listed under '" + contractId + "' is attributed to '" + bar.ContractId
                        + "'. The key is the provenance, and two provenances for one bar cannot be reconciled.",
                        nameof(barsByContract));
                }

                DateOnly tradeDate = calendar.TradeDateFor(bar.OpenTime)
                    ?? DateOnly.FromDateTime(bar.OpenTime.UtcDateTime);

                if (!byTradeDate.TryGetValue(tradeDate, out Dictionary<string, List<Bar>>? byContract))
                {
                    byContract = new Dictionary<string, List<Bar>>(StringComparer.Ordinal);
                    byTradeDate[tradeDate] = byContract;
                }

                if (!byContract.TryGetValue(contractId, out List<Bar>? contractBars))
                {
                    contractBars = [];
                    byContract[contractId] = contractBars;
                }

                contractBars.Add(bar);
            }
        }

        List<TradeDateSelection> selections = new(byTradeDate.Count);
        foreach ((DateOnly tradeDate, Dictionary<string, List<Bar>> byContract) in byTradeDate)
        {
            Dictionary<string, long> volumes = new(StringComparer.Ordinal);
            foreach ((string contractId, List<Bar> bars) in byContract)
            {
                volumes[contractId] = bars.Sum(bar => bar.Volume);
            }

            string winner = storedContractByTradeDate.TryGetValue(tradeDate, out string? stored)
                && byContract.ContainsKey(stored)
                ? stored
                : VolumeWinner(volumes);

            selections.Add(new TradeDateSelection(
                tradeDate,
                winner,
                [.. byContract[winner].OrderBy(bar => bar.OpenTime)],
                volumes));
        }

        return selections;
    }

    /// <summary>
    /// The highest-volume contract; on a tie, the nearest expiry; an unreadable expiry last, then the id.
    /// </summary>
    private static string VolumeWinner(Dictionary<string, long> volumes) =>
        volumes
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => ContractExpiry.TryParseContractId(pair.Key, out ContractExpiry expiry)
                ? expiry.Rank
                : int.MaxValue)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First()
            .Key;
}
