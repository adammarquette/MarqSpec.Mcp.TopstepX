namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One pre-computed indicator value over a named session series — a projection over
/// <see cref="SessionBarRecord"/>, as <see cref="IndicatorValueRecord"/> is over <c>Bars</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is authoritative</b>, on exactly the terms of the resolution flavour: every row is
/// reproducible from the session bars, and a rebuild is a replay (ADR-0006).
/// </para>
/// <para>
/// <b>The series is named, not sized.</b> Where <see cref="IndicatorValueRecord"/> carries
/// <c>ResolutionMinutes</c>, this carries <see cref="Session"/> — <c>rth</c>, <c>globex</c> — because a
/// session is a wall-clock window whose minutes move with daylight saving and whose bars are one a day.
/// </para>
/// <para>
/// <b><see cref="BucketStart"/> is the session's <c>OpenUtc</c>, not its trade date.</b> That diverges from
/// <see cref="SessionBarRecord"/>'s own key deliberately, so a value is keyed by the instant the bar it
/// describes begins, the way every other projection in the store is. The unique
/// <c>(Venue, Instrument, Session, OpenUtc)</c> index on <c>SessionBars</c> is what makes it sound: exactly
/// one session bar per opening, so exactly one bar for any value here to belong to.
/// </para>
/// <para>
/// <b>No <c>ContractId</c>.</b> A read joins <c>SessionBars</c> on the opening for the provenance, and a
/// second copy of it here would be a column free to disagree with the row it describes.
/// </para>
/// <para>
/// Like the session-bar store, and for the same reason as the indicator store, this carries no retention
/// policy: an observation that cited a session's RSI should be checkable against the number actually used.
/// </para>
/// </remarks>
public sealed class SessionIndicatorValueRecord
{
    /// <summary>The venue the underlying bars came from.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol.</summary>
    public required string Instrument { get; set; }

    /// <summary>
    /// The session name the value was computed over, e.g. <c>rth</c> — the same closed vocabulary
    /// <see cref="SessionBarRecord.Session"/> uses, and part of the identity for the same reason the
    /// resolution is over there.
    /// </summary>
    public required string Session { get; set; }

    /// <summary>
    /// The indicator's lowercase stable name, e.g. <c>atr</c>. Part of the key, so a second indicator needs
    /// no reshape — and renaming one orphans every row already written under the old name, where they read
    /// back as an absence rather than an error.
    /// </summary>
    public required string Indicator { get; set; }

    /// <summary>
    /// The period parameter — part of the identity, not a detail. <c>0</c> for indicators that take none,
    /// which keeps them from colliding with a windowed indicator of the same name.
    /// </summary>
    public required int Period { get; set; }

    /// <summary>
    /// The session bar this value belongs to, identified by its opening instant — <c>SessionBars.OpenUtc</c>.
    /// Always UTC.
    /// </summary>
    public required DateTimeOffset BucketStart { get; set; }

    /// <summary>The computed value.</summary>
    public required decimal Value { get; set; }

    /// <summary>
    /// When this value was last computed. Bumped <b>only when <see cref="Value"/> actually changes</b>, so a
    /// rebuild that confirms the existing numbers leaves the timestamps alone and produces an empty diff.
    /// </summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
