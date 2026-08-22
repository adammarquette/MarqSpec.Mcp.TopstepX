namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// Something an agent noticed and wrote down (data dictionary §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The only original data in this database.</b> Bars, indicator values and price levels are all
/// re-derivable — from the vendor, or from the bars. These are not, which makes this the one table worth
/// backing up.
/// </para>
/// <para>
/// Writing one is a write to <i>this</i> store, not to the venue. It does not weaken the read-only boundary
/// (ADR-0002), which is about what reaches the gateway.
/// </para>
/// </remarks>
public sealed class ObservationRecord
{
    /// <summary>The row's identity.</summary>
    public required Guid Id { get; set; }

    /// <summary>
    /// The instrument this is about, or <see langword="null"/> when it is about the market generally.
    /// </summary>
    public string? Instrument { get; set; }

    /// <summary>A short caller-supplied classification, e.g. <c>setup</c>, <c>context</c>, <c>mistake</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The observation itself.</summary>
    public required string Text { get; set; }

    /// <summary>Free-form tags, for filtering that does not need an embedding.</summary>
    public required string[] Tags { get; set; }

    /// <summary>When the observation was recorded.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
