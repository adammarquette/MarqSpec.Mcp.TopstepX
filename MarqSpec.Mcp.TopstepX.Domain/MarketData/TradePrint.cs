namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// One print on the tape, in the venue-neutral shape the footprint aggregator computes over.
/// </summary>
/// <remarks>
/// Pure input: no store identity, no ingest stamp. The host maps <c>TradeRecord</c> onto this
/// before asking for cells, so Domain never sees the entity.
/// </remarks>
/// <param name="Instrument">The normalised venue-neutral symbol, e.g. <c>ES</c>.</param>
/// <param name="ContractId">The venue contract this print belongs to.</param>
/// <param name="TradeTimeUtc">When the venue says the print occurred. Always UTC.</param>
/// <param name="Price">The traded price.</param>
/// <param name="Size">The traded size, in contracts.</param>
/// <param name="Direction">Who lifted. <see cref="TradeDirection.Unknown"/> is refused, not counted.</param>
public sealed record TradePrint(
    string Instrument,
    string ContractId,
    DateTimeOffset TradeTimeUtc,
    decimal Price,
    long Size,
    TradeDirection Direction);
