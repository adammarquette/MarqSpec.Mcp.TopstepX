namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// The latest quote for one contract. One row, replaced in place (gh#351).
/// </summary>
/// <remarks>
/// This is a cache the recorder writes and the server reads. It is not a tape: older quotes are
/// not kept. <see cref="ReceivedAt"/> is <c>clock_timestamp()</c> at upsert. A read ages with
/// <c>clock_timestamp() - ReceivedAt</c>, so both sides are the database clock. A flush delay
/// makes the stamp up to one flush gap later than the hub event. Retention (gh#763) does not
/// apply: the table has no time partition, and a rolled contract's row stays until a later
/// slice deletes it.
/// </remarks>
public sealed class LatestQuoteRecord
{
    /// <summary>The venue. Part of the key.</summary>
    public required string Venue { get; set; }

    /// <summary>The venue contract id, for example <c>CON.F.US.EP.Z26</c>. Part of the key.</summary>
    public required string ContractId { get; set; }

    /// <summary>The normalised instrument symbol, for example <c>ES</c>. Not a contract id.</summary>
    public required string Instrument { get; set; }

    /// <summary>Best bid, in the contract's price units.</summary>
    public required decimal Bid { get; set; }

    /// <summary>Best ask, in the contract's price units.</summary>
    public required decimal Ask { get; set; }

    /// <summary>Last trade, in the contract's price units. Zero means unknown.</summary>
    public required decimal Last { get; set; }

    /// <summary>
    /// When <see cref="Last"/> was seen. Null when last is unknown, including rows written
    /// before this column existed.
    /// </summary>
    public DateTimeOffset? LastAt { get; set; }

    /// <summary>
    /// Session volume from a quote, in contracts. Zero means the quote did not carry it.
    /// A trade print is not session volume.
    /// </summary>
    public required long Volume { get; set; }

    /// <summary>When <see cref="Volume"/> was seen. Null when volume is unknown.</summary>
    public DateTimeOffset? VolumeAt { get; set; }

    /// <summary>
    /// The CME trade date of the event that wrote the row. Null when the market was shut.
    /// A carried last or volume is kept only while this still matches.
    /// </summary>
    public DateOnly? TradeDate { get; set; }

    /// <summary>The venue quote time, UTC, when the payload carried one.</summary>
    public DateTimeOffset? QuoteTimeUtc { get; set; }

    /// <summary>When the database accepted this upsert. Always UTC.</summary>
    public required DateTimeOffset ReceivedAt { get; set; }
}
