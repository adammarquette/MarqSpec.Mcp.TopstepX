using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>
/// The provider registered when no key is configured. It never embeds and never throws.
/// </summary>
/// <remarks>
/// <para>
/// The <b>keyless default</b>, and the reason the rest of Phase 4 can be built before the provider decision is
/// made (gh#44). Observation search falls back to text matching, which is genuinely useful, so an unset key is
/// a supported state rather than a broken one.
/// </para>
/// <para>
/// It is deliberately not a "fake" or a "stub" — nothing should be tested against it. It is the real behaviour
/// of an unconfigured deployment, which is the state this server ships in.
/// </para>
/// </remarks>
/// <param name="options">The embedding options, read only for the model name.</param>
public sealed class UnconfiguredEmbeddingProvider(IOptions<EmbeddingOptions> options) : IEmbeddingProvider
{
    private readonly EmbeddingOptions _options = options.Value;

    /// <inheritdoc />
    public string Model => _options.Model;

    /// <inheritdoc />
    public int Dimensions => TopstepXDbContext.EmbeddingDimensions;

    /// <inheritdoc />
    /// <remarks>
    /// Returns rather than throws, and does so without touching the network. A caller that reached here
    /// despite <see cref="EmbeddingAvailability"/> saying no should still get an answer it can act on.
    /// </remarks>
    public Task<EmbeddingResult> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EmbeddingResult.NotConfigured(Model));
    }
}
