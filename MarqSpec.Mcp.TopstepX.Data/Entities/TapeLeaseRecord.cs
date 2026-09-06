namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One process's exclusive claim on one instrument's tape — the row that makes ADR-0016's
/// "two subscribers on one tape double every volume" a rule the code enforces rather than prose
/// (data dictionary §10, gh#404).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per <c>(Venue, Instrument)</c>, not per store.</b> Two recorders split by
/// <c>MarketData__Instruments</c> are a supported deployment and gh#382 exists to protect it. A
/// whole-store claim would outlaw it; a per-instrument claim refuses only the overlap that doubles
/// volume.
/// </para>
/// <para>
/// <b><see cref="ExpiresAt"/> is the whole expiry answer, and it is read, never assumed.</b> A
/// crashed holder must not strand the tape, so a claim carries an expiry the holder renews. A row
/// whose expiry has not passed is held — not "probably free because nobody answered". The absence
/// of a row is the only free state.
/// </para>
/// <para>
/// <b><see cref="Generation"/> is the concurrency token</b>, so a takeover is one conditional
/// update rather than a read followed by a hopeful write. Two starts that both see the same
/// expired row race the same generation; exactly one update matches, and the loser re-reads and
/// refuses. Without it the reclaim itself is the double-writer it exists to prevent.
/// </para>
/// </remarks>
public sealed class TapeLeaseRecord
{
    /// <summary>The venue the claim covers.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol the claim covers.</summary>
    public required string Instrument { get; set; }

    /// <summary>
    /// Who holds it — a per-process identity, stable for the life of that process and different
    /// on every start, so a restart cannot be mistaken for the process it replaced.
    /// </summary>
    public required string OwnerId { get; set; }

    /// <summary>
    /// Bumped on every acquire and takeover, and checked as a concurrency token on the update, so
    /// two starts reclaiming one expired row cannot both win.
    /// </summary>
    public required long Generation { get; set; }

    /// <summary>When the current holder took the claim. Always UTC.</summary>
    public required DateTimeOffset AcquiredAt { get; set; }

    /// <summary>When the current holder last renewed it. Always UTC.</summary>
    public required DateTimeOffset HeartbeatAt { get; set; }

    /// <summary>
    /// When the claim lapses unless renewed, exclusive. Always UTC. A start may take the row over
    /// only at or after this instant.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }
}
