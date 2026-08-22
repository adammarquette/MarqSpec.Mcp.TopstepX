using Pgvector;

namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>What an embedding belongs to.</summary>
/// <remarks>
/// Explicitly numbered, and <see cref="Unknown"/> is rejected by a database CHECK. An enum whose zero value is
/// storable means a row written before its kind was set is indistinguishable from a valid one.
/// </remarks>
public enum EmbeddingOwnerKind
{
    /// <summary>Unset. Never storable.</summary>
    Unknown = 0,

    /// <summary>An <see cref="ObservationRecord"/>.</summary>
    Observation = 1,
}

/// <summary>
/// One vector embedding (data dictionary §6).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Model"/> is part of the key so that re-embedding under a newer model <b>adds</b> a vector rather
/// than overwriting the old one. Vectors from two models are not comparable, and a table that silently mixed
/// them would return neighbours that are near in no space at all.
/// </para>
/// <para>
/// Indexed with <b>HNSW</b> over cosine distance. HNSW rather than IVFFlat because IVFFlat's lists are only
/// meaningful once it has seen representative data, and this table starts empty — an IVFFlat index built at
/// migration time would be built over nothing. Cosine because embedding models emit direction-normalised
/// vectors, so magnitude carries no signal.
/// </para>
/// <para>
/// This entity is <b>excluded from the model on non-Npgsql providers</b>: nothing else maps
/// <see cref="Vector"/>, and configuring it unconditionally breaks every provider-agnostic test.
/// </para>
/// </remarks>
public sealed class EmbeddingRecord
{
    /// <summary>What this embeds. CHECK: never <see cref="EmbeddingOwnerKind.Unknown"/>.</summary>
    public required EmbeddingOwnerKind OwnerKind { get; set; }

    /// <summary>The owning row's identity, as a string so one table can serve several owner types.</summary>
    public required string OwnerId { get; set; }

    /// <summary>The model that produced the vector. Part of the key.</summary>
    public required string Model { get; set; }

    /// <summary>The vector's dimensionality, recorded so a model change is visible in the data.</summary>
    public required int Dimensions { get; set; }

    /// <summary>The embedding.</summary>
    public required Vector Embedding { get; set; }

    /// <summary>
    /// A hash of the embedded text, so unchanged content is not re-embedded. Embeddings cost money per call,
    /// and a rebuild that re-embeds identical text pays for the same vector twice.
    /// </summary>
    public required string ContentHash { get; set; }

    /// <summary>When the vector was produced.</summary>
    public required DateTimeOffset RecordedAt { get; set; }
}
