namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One quote the recorder is ready to upsert. One row per contract.</summary>
/// <param name="Venue">The venue id.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract id.</param>
/// <param name="Bid">Best bid.</param>
/// <param name="Ask">Best ask.</param>
/// <param name="Last">Last trade.</param>
/// <param name="Volume">Session volume, in contracts.</param>
/// <param name="QuoteTimeUtc">The venue stamp, when the payload had one.</param>
/// <param name="ReceivedAt">
/// When this process accepted the quote. The store stamps the row with the database clock
/// instead, and a read ages on that clock.
/// </param>
public sealed record LatestQuoteDraft(
    string Venue,
    string Instrument,
    string ContractId,
    decimal Bid,
    decimal Ask,
    decimal Last,
    long Volume,
    DateTimeOffset? QuoteTimeUtc,
    DateTimeOffset ReceivedAt);
