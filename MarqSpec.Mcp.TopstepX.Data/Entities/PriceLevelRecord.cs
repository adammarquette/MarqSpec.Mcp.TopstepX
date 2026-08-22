using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// A detected support or resistance zone (data dictionary §4).
/// </summary>
/// <remarks>
/// <para>
/// A synthetic key, unlike everything else in this store, because a zone has no natural one: it is mutable —
/// it widens, gains touches, and changes side as price moves through it — and keying it on its own bounds
/// would mean every update was an insert.
/// </para>
/// <para>
/// The CHECK constraints live in the <b>database</b>, not only in code. This table is written by a geometric
/// detection pass, and an inverted zone is precisely the shape its bugs take — a row where the top is below
/// the bottom reads as entirely plausible everywhere except at the constraint.
/// </para>
/// </remarks>
public sealed class PriceLevelRecord
{
    /// <summary>The row's synthetic identity.</summary>
    public required Guid Id { get; set; }

    /// <summary>The venue the underlying bars came from.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol.</summary>
    public required string Instrument { get; set; }

    /// <summary>The timeframe the level was detected on. A 5-minute level is not an hourly one.</summary>
    public required int TimeframeMinutes { get; set; }

    /// <summary>The zone's lower edge. CHECK: positive.</summary>
    public required decimal Bottom { get; set; }

    /// <summary>The zone's upper edge. CHECK: strictly greater than <see cref="Bottom"/>.</summary>
    public required decimal Top { get; set; }

    /// <summary>
    /// Which side of price the zone sits on. CHECK: never <see cref="KeyLevelKind.Unknown"/> — an unset kind
    /// is not a level, and storing one would put an unlabelled band in front of a reader.
    /// </summary>
    public required KeyLevelKind Kind { get; set; }

    /// <summary>
    /// Prominence in ATR multiples, so a score compares across instruments and volatility regimes. A raw point
    /// distance would not: two points is a wide zone on ES and noise on NQ.
    /// </summary>
    public required decimal Significance { get; set; }

    /// <summary>
    /// When the <b>earliest</b> pivot in this zone formed, kept through merges. A level dates from when it was
    /// first respected, not from the most recent retest — taking the latest would make every old level look
    /// new each time price came back to it.
    /// </summary>
    public required DateTimeOffset FormedAtBucket { get; set; }

    /// <summary>How many pivots fell inside this zone. Summed through merges: more touches, more agreement.</summary>
    public required int TouchCount { get; set; }

    /// <summary>Whether the level is still considered live.</summary>
    public required bool Active { get; set; }

    /// <summary>When this row was last written.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
