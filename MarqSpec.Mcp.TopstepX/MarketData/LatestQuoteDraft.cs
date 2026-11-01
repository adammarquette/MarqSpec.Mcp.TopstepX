namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One quote the recorder is ready to upsert. One row per contract.</summary>
/// <param name="Venue">The venue id.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract id.</param>
/// <param name="Bid">Best bid.</param>
/// <param name="Ask">Best ask.</param>
/// <param name="Last">Last trade.</param>
/// <param name="Volume">Session volume from the quote.</param>
/// <param name="QuoteTimeUtc">The venue stamp, when the payload had one.</param>
/// <param name="ReceivedAt">When this process accepted the quote. Age is measured from here.</param>
public sealed record LatestQuoteDraft(
    string Venue,
    string Instrument,
    string ContractId,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume,
    DateTimeOffset? QuoteTimeUtc,
    DateTimeOffset ReceivedAt);
