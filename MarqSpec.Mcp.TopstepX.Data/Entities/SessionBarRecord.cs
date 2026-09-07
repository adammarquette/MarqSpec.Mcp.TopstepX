namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One named session's OHLCV for one trade date, derived from the stored base bars (ADR-0022).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="TradeDate"/> is the key, not <see cref="OpenUtc"/>.</b> A session is identified by the CME
/// trade date the calendar already models (ADR-0005), and its UTC bounds move with daylight saving — so
/// keying on the instant would make the same session two rows in November.
/// <see cref="OpenUtc"/>/<see cref="CloseUtc"/> are recorded because the caller needs the instants, and they
/// carry a <b>unique</b> index so two rows can never claim one opening.
/// </para>
/// <para>
/// <b><see cref="ContractId"/> is <c>NOT NULL</c> here, unlike <see cref="BarRecord.ContractId"/>.</b> A
/// session bar exists only when every base bucket came from one contract; an unattributable session is
/// <c>ProvenanceUnknown</c> and is not written at all. It is the same asymmetry
/// <see cref="TradeRecord.ContractId"/> already carries — there is no unknown state to represent.
/// </para>
/// <para>
/// <b><see cref="BaseResolutionMinutes"/> and <see cref="WindowCentral"/> are provenance, and they are what
/// makes the row checkable</b> (ADR-0022 §4/§5). A <c>Bar</c> carries no size, so nothing downstream could
/// tell an <c>rth</c> bar built from 30-minute bars from one built from 5-minute bars — this row is the only
/// place the definition that produced it can be stated. A row whose pair disagrees with the definition
/// standing today is <b>discarded</b>, not served.
/// </para>
/// <para>
/// <b>No absence column.</b> A missing session is not stored: it is re-derived on every read, and a stored
/// one would go stale the moment the missing base buckets arrive (ADR-0022, its rejected per-session ledger).
/// </para>
/// </remarks>
public sealed class SessionBarRecord
{
    /// <summary>The venue the base bars came from — the same product on two venues is two series.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol, e.g. <c>ES</c>. Never a contract id.</summary>
    public required string Instrument { get; set; }

    /// <summary>The session's configured name, e.g. <c>rth</c> — the definition's storage key.</summary>
    public required string Session { get; set; }

    /// <summary>The CME trade date this session belongs to. The identity, and a <c>date</c> in the store.</summary>
    public required DateOnly TradeDate { get; set; }

    /// <summary>When the session opened, inclusive. Always UTC. Recorded, not key.</summary>
    public required DateTimeOffset OpenUtc { get; set; }

    /// <summary>When the session closed, exclusive. Always UTC.</summary>
    public required DateTimeOffset CloseUtc { get; set; }

    /// <summary>The first base bucket's open.</summary>
    public required decimal Open { get; set; }

    /// <summary>The highest price traded in the session.</summary>
    public required decimal High { get; set; }

    /// <summary>The lowest price traded in the session.</summary>
    public required decimal Low { get; set; }

    /// <summary>The last base bucket's close.</summary>
    public required decimal Close { get; set; }

    /// <summary>The volume of every base bucket in the session.</summary>
    public required long Volume { get; set; }

    /// <summary>The one venue contract every base bucket came from, e.g. <c>CON.F.US.EP.U26</c>.</summary>
    public required string ContractId { get; set; }

    /// <summary>The size of the base bars this was aggregated from. Provenance: half of what makes it checkable.</summary>
    public required int BaseResolutionMinutes { get; set; }

    /// <summary>How many base buckets the session was built from — every one the calendar expected.</summary>
    public required int BaseBucketCount { get; set; }

    /// <summary>
    /// The session window in Central time, e.g. <c>08:30-15:00</c>. Provenance: the other half, and the
    /// reason a row built under a since-changed definition can be recognised rather than served.
    /// </summary>
    public required string WindowCentral { get; set; }

    /// <summary>When this row was last written or revised.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
