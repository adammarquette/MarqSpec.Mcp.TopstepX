using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>
/// Works out, once at startup, whether embeddings can be produced and stored.
/// </summary>
/// <remarks>
/// Separate from <see cref="EmbeddingAvailability"/> so the answer stays a value and the probing stays a
/// service. The value is what the tools hold; the probe needs a database and a scope, and runs exactly once.
/// </remarks>
public sealed class EmbeddingAvailabilityProbe(ILogger<EmbeddingAvailabilityProbe> logger)
{
    private readonly ILogger<EmbeddingAvailabilityProbe> _logger = logger;

    /// <summary>
    /// Decides availability from the configuration and the live database.
    /// </summary>
    /// <param name="options">The embedding options.</param>
    /// <param name="store">What the startup store probe found.</param>
    /// <param name="database">The store, for the extension check.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What an operator should be told.</returns>
    /// <remarks>
    /// Ordered cheapest first: no key needs no database at all. The extension query only runs when there is
    /// both a key to use and a store to reach, so an unconfigured deployment pays nothing to learn it is
    /// unconfigured.
    /// </remarks>
    public async Task<EmbeddingAvailability> ProbeAsync(
        EmbeddingOptions options,
        StoreAvailability store,
        TopstepXDbContext database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);

        if (!options.IsConfigured)
        {
            return Report(EmbeddingAvailability.NoApiKey());
        }

        if (!store.IsAvailable)
        {
            return Report(EmbeddingAvailability.NoStore());
        }

        if (!database.Database.IsNpgsql())
        {
            // No Npgsql means no vector mapping at all -- the entity is not even in the model.
            return Report(EmbeddingAvailability.NoVectorExtension());
        }

        try
        {
            // pg_extension, not pg_available_extensions: the question is whether it is INSTALLED in this
            // database, not whether it could be. An available-but-not-created extension is exactly the state
            // that would embed happily and then fault at the upsert.
            List<string> installed = await database.Database
                .SqlQuery<string>($"SELECT extname AS \"Value\" FROM pg_extension WHERE extname = 'vector'")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Report(installed.Count > 0
                ? EmbeddingAvailability.Available()
                : EmbeddingAvailability.NoVectorExtension());
        }
        catch (Npgsql.NpgsqlException ex)
        {
            // The store answered a moment ago and does not now. Treat it as no store rather than letting a
            // startup probe take the process down -- this is an optional feature.
            _logger.LogWarning(ex, "Could not check for the vector extension; embeddings disabled.");
            return Report(EmbeddingAvailability.NoStore());
        }
    }

    private EmbeddingAvailability Report(EmbeddingAvailability availability)
    {
        if (availability.IsAvailable)
        {
            _logger.LogInformation("Embeddings are available; observation search will use them.");
        }
        else
        {
            _logger.LogInformation("{Explanation}", availability.Explanation);
        }

        return availability;
    }
}

/// <summary>
/// Carries the startup embedding probe from the composition root to the tools.
/// </summary>
/// <remarks>
/// The same shape as <c>StoreAvailabilityHolder</c>, and for the same reason: the answer is not known until
/// after the host is built, and probing lazily on first resolution would move a database round trip into the
/// middle of a tool call.
/// </remarks>
public sealed class EmbeddingAvailabilityHolder
{
    private EmbeddingAvailability? _value;

    /// <summary>
    /// What the startup probe found.
    /// </summary>
    /// <remarks>
    /// Before the probe runs this reports <b>no key</b> — the conservative answer. Defaulting to "available"
    /// would let a call slip through and pay a vendor before anything had checked there was a store.
    /// </remarks>
    public EmbeddingAvailability Value => _value ?? EmbeddingAvailability.NoApiKey();

    /// <summary>Records the startup probe's result.</summary>
    /// <param name="value">What the probe found.</param>
    public void Set(EmbeddingAvailability value) => _value = value;
}
