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
/// <param name="LastAt">When <paramref name="Last"/> was seen. Null when last is unknown.</param>
/// <param name="VolumeAt">When <paramref name="Volume"/> was seen. Null when volume is unknown.</param>
/// <param name="BookKnown">
/// True when this event carried a bid and an ask, including a zero that means that side is gone.
/// False for a trade, which does not replace the book.
/// </param>
/// <param name="TradeDate">
/// The CME trade date of the event, from <c>BarSessionCalendar.TradeDateFor</c>. Null when the
/// market was shut.
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
    DateTimeOffset ReceivedAt,
    DateTimeOffset? LastAt = null,
    DateTimeOffset? VolumeAt = null,
    bool BookKnown = true,
    DateOnly? TradeDate = null);
