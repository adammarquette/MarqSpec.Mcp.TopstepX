namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// The latest quote for one contract. One row, replaced in place (gh#351).
/// </summary>
/// <remarks>
/// This is a cache the recorder writes and the server reads. It is not a tape: older quotes are
/// not kept, and there is no hub connection on the read. <see cref="ReceivedAt"/> is when the
/// recorder accepted the quote, which is what a later read ages. <see cref="QuoteTimeUtc"/> is
/// the venue stamp when the payload had one.
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

    /// <summary>Last trade, in the contract's price units.</summary>
    public required decimal Last { get; set; }

    /// <summary>Session volume from the quote. Zero is what the venue sent.</summary>
    public required decimal Volume { get; set; }

    /// <summary>The venue quote time, UTC, when the payload carried one.</summary>
    public DateTimeOffset? QuoteTimeUtc { get; set; }

    /// <summary>When the recorder accepted this quote. Always UTC. Age is measured from here.</summary>
    public required DateTimeOffset ReceivedAt { get; set; }
}
