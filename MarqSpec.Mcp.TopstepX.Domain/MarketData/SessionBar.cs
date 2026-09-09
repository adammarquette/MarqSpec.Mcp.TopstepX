namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>Why a trade date carries no session bar.</summary>
/// <remarks>
/// <para>
/// A closed vocabulary, on the same terms as <see cref="TradeDirection"/>: a caller told "absent" must be
/// able to act on <i>why</i>, and a free-text reason is a vocabulary that drifts one caller at a time. The
/// three the aggregator produces are all recoverable — fetch the missing buckets, wait for the roll to leave
/// the window, backfill the provenance — which is what makes naming them worth more than a bare null.
/// </para>
/// <para>
/// <see cref="Unknown"/> is zero so a value that was never set reads as unset rather than as
/// <see cref="Incomplete"/>. <see cref="NotClosed"/> is produced by the host, which owns the clock, and it is
/// declared here anyway: two enums with the same members are two vocabularies free to disagree, and Domain
/// cannot reference the host to share one.
/// </para>
/// </remarks>
public enum SessionBarAbsence
{
    /// <summary>
    /// Unset. Never a reason the aggregator states — a zero default here would name a cause by accident —
    /// and <see cref="SessionBarOutcome.Absent"/> refuses it rather than passing it on.
    /// </summary>
    Unknown = 0,

    /// <summary>The store does not hold every base bucket the calendar expects inside the session window.</summary>
    Incomplete = 1,

    /// <summary>The session's base bars came from more than one contract (ADR-0011).</summary>
    SpansRoll = 2,

    /// <summary>The base bars carry no contract, so the session bar could not say which one it belongs to.</summary>
    ProvenanceUnknown = 3,

    /// <summary>The session had not closed yet. Produced by the host, which owns the clock.</summary>
    NotClosed = 4,
}

/// <summary>
/// One session's OHLCV, derived from the base bars the store holds inside its window.
/// </summary>
/// <remarks>
/// This exists only when the session is <b>complete</b> — see <see cref="SessionBarOutcome"/>. Prices are
/// <see cref="decimal"/> for the reason every price in this assembly is: a session bar accumulates volume
/// over a whole day and its extremes are compared against tick-sized levels.
/// </remarks>
/// <param name="TradeDate">The CME trade date the session belongs to.</param>
/// <param name="OpenUtc">When the session opened, inclusive. Always UTC.</param>
/// <param name="CloseUtc">When the session closed, exclusive. Always UTC.</param>
/// <param name="Open">The first base bucket's open.</param>
/// <param name="High">The highest price traded in the session.</param>
/// <param name="Low">The lowest price traded in the session.</param>
/// <param name="Close">The last base bucket's close.</param>
/// <param name="Volume">The volume of every base bucket in the session.</param>
/// <param name="ContractId">The one contract every base bucket came from.</param>
/// <param name="BaseBucketCount">
/// How many base buckets the session was built from — the count the calendar expected, since a session bar
/// exists only when every one of them was there.
/// </param>
public sealed record SessionBar(
    DateOnly TradeDate,
    DateTimeOffset OpenUtc,
    DateTimeOffset CloseUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string ContractId,
    int BaseBucketCount);

/// <summary>
/// What one requested trade date yielded: a complete <see cref="SessionBar"/>, or an absence with its reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no third shape, and that is the whole design.</b> A session bar assembled from the buckets
/// that happened to be stored is a wrong number wearing an ordinary face: a day missing its opening hour
/// reads as a day that simply opened somewhere else, and nothing downstream can tell the two apart. So the
/// aggregate is either the whole session or nothing, and the nothing carries a reason a caller can act on.
/// </para>
/// <para>
/// Exactly one of <paramref name="Bar"/> and <paramref name="Reason"/> is non-null. Build one through
/// <see cref="Present"/> or <see cref="Absent"/> rather than the constructor, which cannot enforce that.
/// The flat shape is the one every other payload in this assembly uses
/// (<see cref="VolumeProfileRead"/>, <see cref="CoveredTapeWindow"/>) — a base record with two subtypes would
/// be the only hierarchy here, and it buys nothing a null check does not already give.
/// </para>
/// </remarks>
/// <param name="TradeDate">The trade date this outcome answers for.</param>
/// <param name="Bar">The session bar, or <see langword="null"/> when the session is absent.</param>
/// <param name="Reason">Why it is absent, or <see langword="null"/> when it is present.</param>
/// <param name="ExpectedBuckets">How many base buckets the calendar expected inside the session window.</param>
/// <param name="MissingBuckets">
/// How many of them the store did not hold. Zero for a present bar, and zero for an absence that is not about
/// completeness — a spliced or unattributed session is missing nothing.
/// </param>
public sealed record SessionBarOutcome(
    DateOnly TradeDate,
    SessionBar? Bar,
    SessionBarAbsence? Reason,
    int ExpectedBuckets,
    int MissingBuckets)
{
    /// <summary>Whether the session bar exists.</summary>
    public bool IsPresent => Bar is not null;

    /// <summary>A complete session.</summary>
    /// <param name="bar">The session bar.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bar"/> is <see langword="null"/>.</exception>
    public static SessionBarOutcome Present(SessionBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return new SessionBarOutcome(bar.TradeDate, bar, null, bar.BaseBucketCount, 0);
    }

    /// <summary>An absent session, with the reason stated.</summary>
    /// <param name="tradeDate">The trade date.</param>
    /// <param name="reason">
    /// Why there is no session bar. Never <see cref="SessionBarAbsence.Unknown"/>: that member is the
    /// vocabulary's zero, meaning <i>no reason was stated</i>, not a reason a producer may give.
    /// </param>
    /// <param name="expectedBuckets">How many base buckets the calendar expected.</param>
    /// <param name="missingBuckets">How many of them the store did not hold.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reason"/> is <see cref="SessionBarAbsence.Unknown"/>.
    /// </exception>
    /// <remarks>
    /// An absence carrying <see cref="SessionBarAbsence.Unknown"/> would say there is no bar and say nothing
    /// a caller could act on — the bare null the reason vocabulary exists to replace. It is a producer bug
    /// rather than a fourth kind of absence, so it faults here instead of travelling downstream.
    /// </remarks>
    public static SessionBarOutcome Absent(
        DateOnly tradeDate, SessionBarAbsence reason, int expectedBuckets, int missingBuckets)
    {
        if (reason == SessionBarAbsence.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "An absent session states why: SessionBarAbsence.Unknown is the vocabulary's unset zero, not "
                + "a reason. Use Incomplete, SpansRoll, ProvenanceUnknown or NotClosed.");
        }

        return new SessionBarOutcome(tradeDate, null, reason, expectedBuckets, missingBuckets);
    }
}
