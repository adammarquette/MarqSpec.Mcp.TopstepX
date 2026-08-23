namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Another unit of work was writing the same series, and retrying did not get past it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately distinct from <see cref="Venue.VenueException"/> and from a bare
/// <c>PostgresException</c>. The upstream is fine and the request was valid — the store refused to serialise
/// two writers, which is a **transient local condition** and needs to reach a caller as one. Under
/// <c>READ COMMITTED</c> the same collision was a silent last-writer-wins, so this is a fact this server only
/// started having once it began reading a series at <see cref="System.Data.IsolationLevel.RepeatableRead"/>
/// (gh#73).
/// </para>
/// <para>
/// It is thrown only after a bounded retry has already been spent. The first attempt loses to a transaction
/// that committed exactly the work it was missing, so the second runs over a better-informed store and
/// normally succeeds; a second collision means sustained contention on one series rather than a race, and
/// looping on that would hide a real condition rather than survive a transient one.
/// </para>
/// <para>
/// A tool translates this into an <c>McpException</c> at the boundary. It is not thrown as one here: this is a
/// service, and an exception type from the transport has no business in it.
/// </para>
/// </remarks>
public sealed class StoreContentionException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="what">What was being written, in words a caller can act on.</param>
    /// <param name="attempts">How many attempts were spent.</param>
    /// <param name="innerException">The serialization failure underneath.</param>
    public StoreContentionException(string what, int attempts, Exception innerException)
        : base(
            "The store refused to serialise concurrent writes to " + what + " after "
            + attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " attempts. Another request is writing the same series; retry the call, which will be served "
            + "from the store to the extent the other writer has already committed. If this repeats, the "
            + "series is being filled by more than one caller at once and the calls should be serialised.",
            innerException) => Attempts = attempts;

    /// <summary>How many attempts were spent before giving up.</summary>
    public int Attempts { get; }
}
