namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One stored OHLCV bar — the clean-historical system of record (data dictionary §1).
/// </summary>
/// <remarks>
/// <para>
/// This table is what the cache serves from, and it is a <b>record</b> rather than a pipeline: it carries
/// <b>no retention policy</b>, deliberately. A replay reaching for the bars behind a past decision should find
/// what was actually used, not a window that has since aged out.
/// </para>
/// <para>
/// <b>The composite primary key is the idempotence guard.</b> An overlapping re-fetch can only UPDATE the
/// bucket it already wrote, so a vendor revision lands as an update and a missed window heals itself. Nothing
/// needs a de-duplication pass, and there is no way to represent the same bucket twice.
/// </para>
/// <para>
/// <see cref="ResolutionMinutes"/> is part of the key because a 1-minute and a 5-minute bar can open at the
/// same instant. Keyed on time alone they would silently overwrite each other, and the survivor would look
/// entirely ordinary.
/// </para>
/// <para>
/// <b><see cref="ContractId"/> is provenance, not key.</b> The key stays the venue-neutral symbol
/// (ADR-0011): a roll therefore still writes the new contract's bars beside the old one's, but the seam is
/// now recorded, and a read that would cross it says so instead of splicing silently.
/// </para>
/// </remarks>
public sealed class BarRecord
{
    /// <summary>The venue the bars came from — the same product on two venues is two series.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol, e.g. <c>ES</c>. Never a contract id.</summary>
    public required string Instrument { get; set; }

    /// <summary>The bar size in minutes. Part of the key: resolutions are independent series.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>When the bar opened — the hypertable's time dimension, and the bucket's identity. Always UTC.</summary>
    public required DateTimeOffset BucketStart { get; set; }

    /// <summary>The opening price.</summary>
    public required decimal Open { get; set; }

    /// <summary>The highest traded price in the bar.</summary>
    public required decimal High { get; set; }

    /// <summary>The lowest traded price in the bar.</summary>
    public required decimal Low { get; set; }

    /// <summary>The closing price.</summary>
    public required decimal Close { get; set; }

    /// <summary>The traded volume in the bar.</summary>
    public required long Volume { get; set; }

    /// <summary>
    /// The venue contract this bar was fetched from, e.g. <c>CON.F.US.EP.U26</c> — or <see langword="null"/>
    /// when the provenance was never recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nullable, and deliberately never backfilled.</b> Rows written before this column existed carry no
    /// contract, and the information was not captured anywhere at the time — it cannot be recovered. It could
    /// be <i>guessed</i>: contract ids encode an expiry month, and a front-month convention would map a bucket
    /// to a plausible quarter. That guess is precisely the failure this column was added to stop, so the
    /// absence is left visible instead (ADR-0011).
    /// </para>
    /// <para>
    /// Null therefore means <b>unknown</b>, never "the same contract as the row beside it" — an unrecorded run
    /// adjacent to a single recorded contract reports <c>ContractSpan.Unknown</c> ("cannot tell"), not a
    /// promotion to a roll on its own. It does not erase a roll the store can already prove, either: two runs
    /// whose contract id is recorded and different are a roll (<c>SpansRoll</c>) even when an unattributed run
    /// sits beside or between them (ADR-0011, gh#402). A read that touches a null bucket re-asks the venue and
    /// the existing upsert overwrites it, so this heals on its own rather than only by deleting and refetching
    /// by hand.
    /// </para>
    /// <para>
    /// Not <c>required</c>, unlike its siblings: a required member would make the unknown state
    /// unrepresentable, and the whole point is that it is a state the store is genuinely in.
    /// </para>
    /// </remarks>
    public string? ContractId { get; set; }

    /// <summary>When this row was last written or revised.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
