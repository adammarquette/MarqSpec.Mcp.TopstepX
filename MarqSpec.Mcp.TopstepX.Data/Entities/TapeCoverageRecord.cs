namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// A range during which a subscription was actually <b>listening</b> — the tape's coverage ledger
/// (data dictionary §8).
/// </summary>
/// <remarks>
/// <para>
/// Written from subscription lifecycle, not inferred from rows. A quiet market and a dead
/// subscription produce the same empty range, and only lifecycle can tell them apart — the same
/// third-state role <see cref="BarCoverageRecord"/> plays for bars (gh#215, gh#217). A hole here
/// is permanent: there is no market-tape REST backfill (ADR-0016).
/// </para>
/// <para>
/// <b>The range is half-open, <c>[RangeStart, RangeEnd)</c>.</b> An open listen is stored with
/// <see cref="StillListeningEnd"/> until the close path replaces it. Closed ranges written
/// adjacently either overlap by one instant or leave a hole, and both errors are invisible until
/// a profile reports a window that was never covered.
/// </para>
/// <para>
/// <see cref="ContractId"/> is in the key: listening is per contract, and a roll means the
/// intended subscription set changed.
/// </para>
/// </remarks>
public sealed class TapeCoverageRecord
{
    /// <summary>
    /// Exclusive end stored while the listen is still open. Replaced with the real exclusive
    /// end when the range closes. A leftover row with this end is discarded on the next start
    /// so a crash cannot claim coverage after death.
    /// </summary>
    public static readonly DateTimeOffset StillListeningEnd =
        new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The venue that was subscribed.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol.</summary>
    public required string Instrument { get; set; }

    /// <summary>The venue contract the subscription was listening to.</summary>
    public required string ContractId { get; set; }

    /// <summary>The start of the listening range, inclusive. Always UTC.</summary>
    public required DateTimeOffset RangeStart { get; set; }

    /// <summary>The end of the listening range, exclusive. Always UTC.</summary>
    public required DateTimeOffset RangeEnd { get; set; }

    /// <summary>When this range was last written.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
