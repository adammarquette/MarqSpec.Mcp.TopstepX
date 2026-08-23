namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>Why embeddings are unavailable, when they are.</summary>
public enum EmbeddingUnavailableReason
{
    /// <summary>They are available.</summary>
    None = 0,

    /// <summary>No API key. The expected state until Phase 4 is configured.</summary>
    NoApiKey = 1,

    /// <summary>The database is not reachable, so there is nowhere to put a vector.</summary>
    NoStore = 2,

    /// <summary>The database is reachable but the <c>vector</c> extension is not installed.</summary>
    NoVectorExtension = 3,

    /// <summary>The <c>vector</c> extension is installed but predates what semantic search needs.</summary>
    VectorExtensionTooOld = 4,
}

/// <summary>
/// Whether embeddings can be produced <b>and stored</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Availability means a key AND somewhere to put the vector.</b> That conjunction is the whole point of
/// this type, and it is not obvious enough to leave implied: a deployment with a key but no <c>vector</c>
/// extension will embed at real cost on every write and then fault at the upsert. Paying a vendor per call for
/// vectors that cannot be stored is worse than not embedding at all, and it is invisible until the bill
/// arrives.
/// </para>
/// <para>
/// Probed <b>once at startup</b>, not per call. A network round trip in the middle of a tool call to answer
/// "is this configured" is a cost paid on every request to learn something that changes on restart. It also
/// means two calls in the same session can never disagree.
/// </para>
/// <para>
/// The same shape as <c>StoreAvailability</c>, and for the same reason: an absent dependency degrades to a
/// clear answer at the point of use rather than to an exception or a surprise charge.
/// </para>
/// </remarks>
public sealed class EmbeddingAvailability
{
    private EmbeddingAvailability(EmbeddingUnavailableReason reason, string? explanation)
    {
        Reason = reason;
        Explanation = explanation;
    }

    /// <summary>Whether an embedding can be produced and stored.</summary>
    public bool IsAvailable => Reason == EmbeddingUnavailableReason.None;

    /// <summary>Why not, when not.</summary>
    public EmbeddingUnavailableReason Reason { get; }

    /// <summary>A sentence naming the fix, or <see langword="null"/> when available.</summary>
    public string? Explanation { get; }

    /// <summary>Embeddings can be produced and stored.</summary>
    /// <returns>An available marker.</returns>
    public static EmbeddingAvailability Available() =>
        new(EmbeddingUnavailableReason.None, null);

    /// <summary>No key is configured.</summary>
    /// <returns>An unavailable marker.</returns>
    /// <remarks>
    /// Phrased as a state rather than a fault. This is what the server ships as, and an operator reading it
    /// should understand that search still works.
    /// </remarks>
    public static EmbeddingAvailability NoApiKey() =>
        new(
            EmbeddingUnavailableReason.NoApiKey,
            "No embedding key is configured, so observation search matches text rather than meaning. Set "
            + "Embeddings__ApiKey to enable semantic search. This is a supported state, not a fault.");

    /// <summary>The store is unreachable.</summary>
    /// <returns>An unavailable marker.</returns>
    public static EmbeddingAvailability NoStore() =>
        new(
            EmbeddingUnavailableReason.NoStore,
            "The database is not reachable, so a vector could be produced but not stored. Embedding is "
            + "disabled rather than paid for and discarded.");

    /// <summary>The <c>vector</c> extension is missing.</summary>
    /// <returns>An unavailable marker.</returns>
    public static EmbeddingAvailability NoVectorExtension() =>
        new(
            EmbeddingUnavailableReason.NoVectorExtension,
            "The database is reachable but the 'vector' extension is not installed, so there is nowhere to "
            + "put an embedding. Embedding is disabled rather than paid for and discarded. The compose stack "
            + "runs timescale/timescaledb-ha, which bundles it; a plain Postgres does not.");

    /// <summary>The lowest <c>vector</c> version semantic search can run on.</summary>
    /// <remarks>
    /// <para>
    /// <b>0.8 is where <c>hnsw.iterative_scan</c> arrives</b>, and search is not correct without it: an HNSW
    /// scan visits a fixed number of candidates and applies remaining filters afterwards, so a filtered
    /// search silently returns a short list rather than an error.
    /// </para>
    /// <para>
    /// Verified on 0.7.4 rather than assumed — <c>SET LOCAL hnsw.iterative_scan</c> there raises
    /// <c>invalid configuration parameter name</c> ("hnsw" is a reserved prefix) <b>and aborts the
    /// transaction</b>. Reaching that at query time turns a search into an exception, which is precisely what
    /// this server's fallback contract says must not happen.
    /// </para>
    /// </remarks>
    public static readonly Version MinimumVectorVersion = new(0, 8);

    /// <summary>The <c>vector</c> extension is older than semantic search requires.</summary>
    /// <param name="installed">The version actually installed, for the message.</param>
    /// <returns>An unavailable marker.</returns>
    /// <remarks>
    /// Refuses the whole vector path rather than running it without the iterative scan. Unfiltered search
    /// would work on an older pgvector; a filtered one would quietly return fewer rows than exist, and a
    /// quietly-short answer is worse than an honest substring match.
    /// </remarks>
    public static EmbeddingAvailability VectorExtensionTooOld(string installed) =>
        new(
            EmbeddingUnavailableReason.VectorExtensionTooOld,
            $"The 'vector' extension is version {installed}, and semantic search needs "
            + $"{MinimumVectorVersion.Major}.{MinimumVectorVersion.Minor} or newer for hnsw.iterative_scan - "
            + "without it a filtered search silently returns fewer results than exist. Observation search "
            + "matches text instead, which is correct if less precise. Upgrade pgvector to enable it.");
}
