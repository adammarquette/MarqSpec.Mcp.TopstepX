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

    /// <summary>When this row was last written or revised.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
