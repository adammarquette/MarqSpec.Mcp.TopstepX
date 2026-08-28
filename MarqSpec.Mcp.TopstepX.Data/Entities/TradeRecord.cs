namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>Which side of the tape produced a print.</summary>
/// <remarks>
/// <para>
/// Zero is <see cref="Unknown"/>, and unlike <see cref="EmbeddingOwnerKind"/> it <b>is</b> storable.
/// An unstated or unparseable venue direction must remain missing rather than silently become a buy
/// (<c>TradeLogType.Buy = 0</c> is the trap this exists to survive — gh#213, gh#220).
/// </para>
/// </remarks>
public enum TradeDirection
{
    /// <summary>The direction could not be determined. Stored, never defaulted to a side.</summary>
    Unknown = 0,

    /// <summary>The aggressor was lifting (buying).</summary>
    Buy = 1,

    /// <summary>The aggressor was hitting (selling).</summary>
    Sell = 2,
}

/// <summary>
/// One print on the trade tape — the order-flow system of record (data dictionary §7).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ContractId"/> is in the key, unlike <see cref="BarRecord"/>.</b> A tape row without a
/// contract cannot be attributed at all, so it has no meaning. Bars keep the venue-neutral symbol as
/// the key and record the contract beside it; here the contract is identity (gh#215).
/// </para>
/// <para>
/// <b><see cref="Sequence"/> exists because the venue supplies no trade id.</b> Two prints share a
/// millisecond routinely, so without a tiebreak the primary key silently collapses them and the
/// survivor looks like an ordinary trade. It is ingest-assigned and monotonic per
/// <c>(instrument, contract)</c> — not a venue value.
/// </para>
/// <para>
/// This table is a record rather than a pipeline: it carries <b>no retention policy</b>, and it is
/// the store's first compression policy. A replay reaching for the prints behind a past footprint
/// should find what was actually used; compression shrinks the chunks without deleting them
/// (ADR-0004).
/// </para>
/// </remarks>
public sealed class TradeRecord
{
    /// <summary>The venue the print came from — the same product on two venues is two series.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol, e.g. <c>ES</c>. Never a contract id.</summary>
    public required string Instrument { get; set; }

    /// <summary>
    /// The venue contract this print belongs to, e.g. <c>CON.F.US.EP.U26</c>. Required: a print
    /// without a contract cannot be attributed.
    /// </summary>
    public required string ContractId { get; set; }

    /// <summary>When the venue says the print occurred — the hypertable's time dimension. Always UTC.</summary>
    public required DateTimeOffset TradeTimeUtc { get; set; }

    /// <summary>
    /// Ingest-assigned tiebreak, monotonic per <c>(instrument, contract)</c>. Not a venue trade id.
    /// </summary>
    public required long Sequence { get; set; }

    /// <summary>The traded price.</summary>
    public required decimal Price { get; set; }

    /// <summary>The traded size, in contracts.</summary>
    public required long Size { get; set; }

    /// <summary>
    /// Who lifted. <see cref="TradeDirection.Unknown"/> is a stored fact, never rewritten to a side.
    /// </summary>
    public required TradeDirection Direction { get; set; }

    /// <summary>When this process received the print. Always UTC. Not the venue's stamp.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
