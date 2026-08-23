namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>What a piece of text is being embedded <i>for</i>.</summary>
/// <remarks>
/// <para>
/// <b>Not a hint. It changes the vector.</b> Cohere's models embed a stored document and a search query into
/// deliberately different regions, and the API requires the caller to say which. Using one value for both
/// returns perfectly well-formed vectors and degrades retrieval <i>measurably</i> — a plausible answer rather
/// than an error, which is the failure mode this repository keeps meeting (ADR-0009).
/// </para>
/// <para>
/// It is a required parameter rather than a defaulted one for exactly that reason: a default here would be
/// wrong half the time and never say so.
/// </para>
/// </remarks>
public enum EmbeddingPurpose
{
    /// <summary>Unset. Never valid — a provider refuses rather than guessing.</summary>
    Unknown = 0,

    /// <summary>Text being stored for later retrieval. Cohere's <c>search_document</c>.</summary>
    Document = 1,

    /// <summary>A query being matched against stored text. Cohere's <c>search_query</c>.</summary>
    Query = 2,
}

/// <summary>
/// Turns text into a vector, or explains why it did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>An implementation must not throw for anything an operator could reasonably hit.</b> No key, a rate
/// limit, a timeout, a malformed response — each is an <see cref="EmbeddingOutcome"/> and a fall back to text
/// search, because observation search works without embeddings and must keep working when they are absent.
/// </para>
/// <para>
/// The one exception is the caller's own <see cref="CancellationToken"/>, which propagates. That is not a
/// failure of the provider; it is the caller changing their mind, and swallowing it would leave the caller
/// waiting on work it already abandoned.
/// </para>
/// <para>
/// Availability is <b>not</b> asked of the provider. It is a property of the deployment — a key AND somewhere
/// to put the vector — and lives in <see cref="EmbeddingAvailability"/>, decided once at startup. A provider
/// that reported its own availability would have to probe the database, which is not its job.
/// </para>
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>The model this provider embeds with, stored beside every vector it produces.</summary>
    string Model { get; }

    /// <summary>The width of the vectors it produces.</summary>
    /// <remarks>
    /// Checked against the column's width before anything is stored. A provider whose output does not fit is
    /// a configuration error worth catching at the seam, not a truncation.
    /// </remarks>
    int Dimensions { get; }

    /// <summary>
    /// Embeds one piece of text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="purpose">
    /// Whether this is a document being stored or a query being matched. <b>Required</b>, because it changes
    /// the vector rather than merely describing it.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The vector, or the reason there is not one.</returns>
    Task<EmbeddingResult> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken);
}
