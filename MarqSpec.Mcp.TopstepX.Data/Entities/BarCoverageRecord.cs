namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// A range the venue was asked for and answered <b>empty</b> — the negative-result ledger (data dictionary §3).
/// </summary>
/// <remarks>
/// <para>
/// This is the one table with no counterpart in <c>trading-copilot</c>, and the reason is worth stating: that
/// system's backfill polls a fixed watchlist on a timer, so it never faces "an agent asked for an arbitrary
/// cold range twice in a row". A cache driven by agent queries faces it on every call.
/// </para>
/// <para>
/// The session calendar removes weekends, maintenance windows and holidays from "expected". What it cannot
/// remove is a range that is genuinely a session and genuinely has no data — before the contract listed, a
/// session the exchange cancelled, a window the vendor's own history does not reach. Those are
/// <i>expected by the calendar and absent from the store</i>, which is indistinguishable from "not fetched
/// yet". This table is the third state (ADR-0005).
/// </para>
/// <para>
/// <b>The range is half-open, <c>[RangeStart, RangeEnd)</c>.</b> Closed ranges written adjacently either
/// overlap by one bucket or leave a one-bucket hole, and both errors are invisible until something derived
/// from the wrong bar produces a number nobody can reproduce.
/// </para>
/// </remarks>
public sealed class BarCoverageRecord
{
    /// <summary>The venue that was asked.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised venue-neutral instrument symbol.</summary>
    public required string Instrument { get; set; }

    /// <summary>The bar size in minutes.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>The start of the empty range, inclusive. Always UTC.</summary>
    public required DateTimeOffset RangeStart { get; set; }

    /// <summary>The end of the empty range, exclusive. Always UTC.</summary>
    public required DateTimeOffset RangeEnd { get; set; }

    /// <summary>When the venue was asked.</summary>
    public required DateTimeOffset RecordedAt { get; set; }

    /// <summary>
    /// When this claim stops being believed, or <see langword="null"/> for never.
    /// </summary>
    /// <remarks>
    /// Deliberately asymmetric. Near <c>now</c> this is short, because a bucket that is empty only for not
    /// having printed yet will print shortly and a permanent claim would blind the cache to it. For settled
    /// history it is <see langword="null"/>, because a hole in 2024 is not going to fill in and re-asking
    /// costs a request per call forever.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; set; }
}
