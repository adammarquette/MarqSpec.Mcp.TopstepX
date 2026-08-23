namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One pre-computed indicator value — a projection over <see cref="BarRecord"/> (data dictionary §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is authoritative.</b> Every row is reproducible from the bar store, and that is the point:
/// a rebuild is a replay, and adding an indicator needs no new vendor data (ADR-0006).
/// </para>
/// <para>
/// The key carries <b>indicator and period</b> as well as the series, because an ATR(14) and an ATR(3) are
/// different numbers over the same bars, and a consumer asking for one must never be handed the other.
/// </para>
/// <para>
/// Like the bar store, and for the same reason, this carries no retention policy: an observation that cited an
/// ATR should be checkable against the number that was actually used.
/// </para>
/// </remarks>
public sealed class IndicatorValueRecord
{
    /// <summary>The venue the underlying bars came from.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol.</summary>
    public required string Instrument { get; set; }

    /// <summary>The bar size the indicator was computed over, in minutes.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>
    /// The indicator's lowercase stable name, e.g. <c>atr</c>. Part of the key, so a second indicator needs no
    /// reshape — and renaming one orphans every row already written under the old name, where they read back
    /// as an absence rather than an error.
    /// </summary>
    public required string Indicator { get; set; }

    /// <summary>
    /// The period parameter — part of the identity, not a detail. <c>0</c> for indicators that take none, such
    /// as session-anchored VWAP, which keeps them from colliding with a windowed indicator of the same name.
    /// </summary>
    public required int Period { get; set; }

    /// <summary>The bar bucket this value belongs to. Always UTC.</summary>
    public required DateTimeOffset BucketStart { get; set; }

    /// <summary>The computed value.</summary>
    public required decimal Value { get; set; }

    /// <summary>
    /// When this value was last computed. Bumped <b>only when <see cref="Value"/> actually changes</b>, so a
    /// rebuild that confirms the existing numbers leaves the timestamps alone and produces an empty diff.
    /// </summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
