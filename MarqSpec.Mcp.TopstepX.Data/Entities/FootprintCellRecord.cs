namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// Buy and sell volume at one price inside one bar — a projection over <see cref="TradeRecord"/>
/// (data dictionary §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is authoritative.</b> Every row is reproducible from the tape, and that is the
/// point: a rebuild is a replay (ADR-0006, gh#220).
/// </para>
/// <para>
/// <b>There is no <c>ContractId</c> here, and that is deliberate.</b> A cell is always computed
/// inside a single contract run — the projection never smooths across a roll — so the contract is
/// a property of the trades in the bucket, and duplicating it would be a second copy of a fact
/// that can disagree with the first. The asymmetry with <see cref="TradeRecord"/> (where the
/// contract <i>is</i> the key) is the one the data dictionary has to state rather than leave to
/// be noticed.
/// </para>
/// </remarks>
public sealed class FootprintCellRecord
{
    /// <summary>The venue the underlying prints came from.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol.</summary>
    public required string Instrument { get; set; }

    /// <summary>The bar size this cell was aggregated into, in minutes.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>When the bar opened. Always UTC. Aligns with <see cref="BarRecord.BucketStart"/>.</summary>
    public required DateTimeOffset BucketStart { get; set; }

    /// <summary>The price level inside the bar.</summary>
    public required decimal Price { get; set; }

    /// <summary>Volume whose aggressor was lifting.</summary>
    public required long BuyVolume { get; set; }

    /// <summary>Volume whose aggressor was hitting.</summary>
    public required long SellVolume { get; set; }

    /// <summary>When this cell was last computed.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
