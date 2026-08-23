namespace MarqSpec.Mcp.TopstepX.Embeddings;

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
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The vector, or the reason there is not one.</returns>
    Task<EmbeddingResult> EmbedAsync(string text, CancellationToken cancellationToken);
}
