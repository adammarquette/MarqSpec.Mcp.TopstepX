using System.Globalization;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The host comparison of two answers for which contract is in front (gh#219).
/// </summary>
/// <param name="Tape">Highest session volume on stored <c>Trades</c>.</param>
/// <param name="GatewaySelectedContractId">
/// <c>ResolveContractsAsync</c>'s first result — the contract <c>BarCacheService</c> fetches.
/// Null when the venue named none.
/// </param>
/// <param name="GatewayMarkedActiveContractIds">
/// Every contract the venue flagged <c>IsActive</c>. Search often marks every hit, so this list
/// can be longer than one without naming a unique front.
/// </param>
/// <param name="Agree">
/// Whether the tape's unique front and the gateway's selected contract are the same id.
/// </param>
/// <param name="Used">
/// <see cref="UsedTapeVolume"/> when the tape named a unique front;
/// <see cref="UsedNone"/> when it did not. The gateway is never substituted.
/// </param>
/// <param name="Why">Which answer is being used, and why the other was not dropped.</param>
public sealed record TapeVolumeFrontRead(
    VolumeFront Tape,
    string? GatewaySelectedContractId,
    IReadOnlyList<string> GatewayMarkedActiveContractIds,
    bool Agree,
    string Used,
    string Why)
{
    /// <summary>This read's front is the highest-volume contract on the tape.</summary>
    public const string UsedTapeVolume = "tape-volume";

    /// <summary>The tape named no unique front; the gateway answer is reported, not used.</summary>
    public const string UsedNone = "none";
}

/// <summary>
/// Reads stored prints and compares the tape's volume-front with the gateway's selected contract.
/// </summary>
/// <remarks>
/// <para>
/// The host owns the store and the venue seam; Domain measures the prints it is handed.
/// Choosing the front is a read. Nothing here writes <c>Trades</c>, and nothing filters at
/// ingest — both contracts stay on the tape across a roll (gh#219).
/// </para>
/// <para>
/// <b>Do not treat this as a second silent source of truth.</b> During a roll the two answers
/// disagree by design. The payload names both and says the tape is the volume-front. Profile
/// <c>contracts</c> from cells and <c>TapeCoverage</c> is a third cut (newest listening run)
/// and is not this number.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="gateway">The venue seam — used only to read what Bars would fetch.</param>
/// <param name="calendar">The session calendar, a parsed value, not a live clock.</param>
public sealed class TapeVolumeFrontService(
    TopstepXDbContext database,
    IMarketDataGateway gateway,
    BarSessionCalendar calendar)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IMarketDataGateway _gateway = gateway;
    private readonly BarSessionCalendar _calendar = calendar;

    /// <summary>
    /// Measures the tape's volume-front for <paramref name="instrument"/> and places it next to
    /// the gateway's selected contract.
    /// </summary>
    /// <param name="instrument">The instrument.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="asOfUtc">
    /// When set, only prints at or before this instant take part. Omitted reads the whole tape.
    /// </param>
    /// <param name="resolveGateway">
    /// Whether to ask the venue for the contract Bars would fetch. The venue has no
    /// historical pick: a past <paramref name="asOfUtc"/> must pass <see langword="false"/>
    /// rather than date today's answer as if it were as-of.
    /// </param>
    /// <returns>Both answers, which one this read uses, and why.</returns>
    public async Task<TapeVolumeFrontRead> ReadAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken,
        DateTimeOffset? asOfUtc = null,
        bool resolveGateway = true)
    {
        string venue = _gateway.VenueId;

        IQueryable<TradeRecord> query = _database.Trades
            .AsNoTracking()
            .Where(trade => trade.Venue == venue && trade.Instrument == instrument.Symbol);

        if (asOfUtc is { } until)
        {
            DateTimeOffset cutoff = until.ToUniversalTime();
            query = query.Where(trade => trade.TradeTimeUtc <= cutoff);
        }

        List<TradeRecord> rows = await query
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<TradePrint> prints =
        [
            .. rows.Select(row => new TradePrint(
                row.Instrument,
                row.ContractId,
                row.TradeTimeUtc,
                row.Price,
                row.Size,
                row.Direction)),
        ];

        VolumeFront tape = TapeVolumeFront.Measure(prints, _calendar);

        if (!resolveGateway)
        {
            return new TapeVolumeFrontRead(
                tape,
                null,
                [],
                Agree: false,
                tape.ActiveContractId is null
                    ? TapeVolumeFrontRead.UsedNone
                    : TapeVolumeFrontRead.UsedTapeVolume,
                Why: string.Empty);
        }

        IReadOnlyList<VenueContract> contracts = await _gateway
            .ResolveContractsAsync(instrument, cancellationToken)
            .ConfigureAwait(false);

        string? selected = contracts.Count == 0 ? null : contracts[0].ContractId;
        IReadOnlyList<string> markedActive =
        [
            .. contracts.Where(contract => contract.IsActive).Select(contract => contract.ContractId),
        ];

        (bool agree, string used, string why) = Compare(tape, selected);

        return new TapeVolumeFrontRead(tape, selected, markedActive, agree, used, why);
    }

    private static (bool Agree, string Used, string Why) Compare(VolumeFront tape, string? gatewaySelected)
    {
        if (tape.ActiveContractId is null)
        {
            if (gatewaySelected is null)
            {
                return (
                    true,
                    TapeVolumeFrontRead.UsedNone,
                    "The tape has no session volume and the gateway named no contract, so there is no front to report.");
            }

            return (
                false,
                TapeVolumeFrontRead.UsedNone,
                "The tape has no session volume, so there is no volume-front. The gateway selected "
                + gatewaySelected
                + " (the contract Bars would fetch). This read does not substitute the gateway answer.");
        }

        if (gatewaySelected is null)
        {
            return (
                false,
                TapeVolumeFrontRead.UsedTapeVolume,
                "Tape volume in session "
                + Format(tape.ActiveSessionDate)
                + " names "
                + tape.ActiveContractId
                + " as the front (highest volume). The gateway named no contract. This read uses the tape.");
        }

        if (string.Equals(tape.ActiveContractId, gatewaySelected, StringComparison.Ordinal))
        {
            return (
                true,
                TapeVolumeFrontRead.UsedTapeVolume,
                "Tape volume and the gateway both name " + tape.ActiveContractId + " as the front.");
        }

        return (
            false,
            TapeVolumeFrontRead.UsedTapeVolume,
            "Tape volume in session "
            + Format(tape.ActiveSessionDate)
            + " names "
            + tape.ActiveContractId
            + " as the front (highest volume). The gateway selected "
            + gatewaySelected
            + " (the contract Bars would fetch). Neither is dropped; this read uses the tape.");
    }

    private static string Format(DateOnly? session) =>
        session?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
}
