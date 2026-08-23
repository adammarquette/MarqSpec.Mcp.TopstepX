namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The embedding provider's settings.
/// </summary>
/// <remarks>
/// <b>An unset key is a supported state, not a broken one.</b> Observation search falls back to text matching,
/// which is honest and useful on its own — so nothing here is required, and nothing here fails startup.
/// </remarks>
public sealed class EmbeddingOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Embeddings";

    /// <summary>The provider API key. Blank means embeddings are off and search matches text.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The model name, stored alongside every vector.
    /// </summary>
    /// <remarks>
    /// Part of the <c>Embeddings</c> primary key, so re-embedding under a new model <b>adds</b> a vector
    /// rather than overwriting the old one. Vectors from two models are not comparable, and a table that
    /// silently mixed them would return neighbours that are near in no space at all.
    /// <para>
    /// <b>The width is not configurable and is deliberately not here.</b> It is pinned to
    /// <see cref="Data.TopstepXDbContext.EmbeddingDimensions"/>, because the database column is
    /// <c>vector(1024)</c> and a mismatch is a migration rather than a setting. Note that
    /// <c>embed-v4.0</c> defaults to <b>1536</b>, so a call that omits the width is wrong by default
    /// (ADR-0009).
    /// </para>
    /// </remarks>
    public string Model { get; init; } = "embed-v4.0";

    /// <summary>Whether a key is present.</summary>
    /// <remarks>
    /// Necessary but <b>not sufficient</b>. Availability also requires somewhere to put the vector — see
    /// <c>EmbeddingAvailability</c>. A key without a vector store means paying a vendor per call and then
    /// faulting at the upsert, which is worse than not embedding at all.
    /// </remarks>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
