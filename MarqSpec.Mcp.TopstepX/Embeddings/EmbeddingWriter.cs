using System.Security.Cryptography;
using System.Text;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>
/// Stores the vector for an observation, buying one only when it has to.
/// </summary>
/// <remarks>
/// <para>
/// Embeddings cost money per call, so this does two things before reaching for the provider: it refuses when
/// embeddings are unavailable at all, and it <b>reuses an existing vector for identical text</b>. Identical
/// text embedded <i>for the same purpose</i> under the same model produces an identical vector, so paying
/// twice buys nothing — and the purpose half of that is carried by <c>OwnerKind</c>, which is why the lookup
/// filters on it. See the comment on the lookup itself.
/// </para>
/// <para>
/// It never throws for a provider failure. A rate limit or an outage leaves the observation stored without a
/// vector — the observation is the durable thing, and the vector is an index over it that can be rebuilt.
/// </para>
/// </remarks>
/// <param name="database">The store.</param>
/// <param name="provider">The embedding provider.</param>
/// <param name="availability">Whether embeddings can be produced and stored.</param>
/// <param name="logger">The logger.</param>
public sealed class EmbeddingWriter(
    TopstepXDbContext database,
    IEmbeddingProvider provider,
    EmbeddingAvailabilityHolder availability,
    ILogger<EmbeddingWriter> logger)
{
    private readonly TopstepXDbContext _database = database;
    private readonly IEmbeddingProvider _provider = provider;
    private readonly EmbeddingAvailabilityHolder _availability = availability;
    private readonly ILogger<EmbeddingWriter> _logger = logger;

    /// <summary>
    /// Hashes text for the reuse guard.
    /// </summary>
    /// <param name="text">The text, <b>exactly as it will be stored</b>.</param>
    /// <returns>A lowercase hex SHA-256.</returns>
    /// <remarks>
    /// <para>
    /// <b>Hash what is stored, never what was handed in.</b> The observation's text is trimmed before it is
    /// written, so hashing the raw input would produce a hash that does not describe the stored row — and the
    /// guard would miss a match it should have found, silently buying a vector it already had.
    /// </para>
    /// <para>
    /// That is the same shape as gh#37, where a comparison between a full-precision computation and a rounded
    /// stored value could never be equal and made a "has it changed?" guard answer yes forever. The fix there
    /// and the rule here are the same one: <b>compare like with like, and derive both sides from the stored
    /// form.</b>
    /// </para>
    /// <para>
    /// UTF-8 explicitly, not the platform default: a hash that varies by machine is not a hash.
    /// </para>
    /// </remarks>
    public static string HashOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Ensures an observation has a stored vector, if one can be had.
    /// </summary>
    /// <param name="observation">The observation, already added to the context.</param>
    /// <param name="now">The instant to stamp.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>What happened, for the caller to report.</returns>
    /// <remarks>
    /// Does <b>not</b> call <c>SaveChanges</c>: the caller owns the unit of work, so the observation and its
    /// vector commit together or not at all.
    /// </remarks>
    public async Task<EmbeddingOutcome> EnsureEmbeddedAsync(
        ObservationRecord observation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!_availability.Value.IsAvailable)
        {
            // No key, no store, or no vector extension. Skipped WITHOUT a call -- the whole point of probing
            // availability at startup is that this path costs nothing.
            return EmbeddingOutcome.NotConfigured;
        }

        string hash = HashOf(observation.Text);
        string ownerId = observation.Id.ToString();

        // Reuse before buying. An agent recording a recurring note should not pay for it every time.
        //
        // OwnerKind IS PART OF THE PREDICATE, and not for tidiness. The premise "identical text under one
        // model is an identical vector" is FALSE in general: EmbeddingPurpose changes the vector, so a
        // search_query and a search_document embedding of the same text under the same model differ, and
        // EmbeddingRecord stores nothing that tells them apart. Only OwnerKind does -- an Observation row is
        // a document embedding by construction, because that is the only thing that writes one.
        //
        // Without this clause the guard would be correct only by accident of what currently writes to the
        // table. Anything that later stores query vectors MUST file them under a different OwnerKind; storing
        // one as an Observation would hand the next identical note a well-formed vector from the wrong
        // region, which retrieves measurably worse and reports nothing.
        EmbeddingRecord? twin = await _database.Embeddings
            .FirstOrDefaultAsync(
                e => e.OwnerKind == EmbeddingOwnerKind.Observation
                    && e.ContentHash == hash
                    && e.Model == _provider.Model,
                cancellationToken)
            .ConfigureAwait(false);

        if (twin is not null)
        {
            await UpsertAsync(ownerId, twin.Embedding, hash, now, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Reused an existing vector for identical text; no embedding call made.");
            return EmbeddingOutcome.Succeeded;
        }

        EmbeddingResult result = await _provider
            .EmbedAsync(observation.Text, EmbeddingPurpose.Document, cancellationToken)
            .ConfigureAwait(false);

        if (!result.HasVector)
        {
            // The observation still lands. A vector is an index over it, and an index can be rebuilt; the
            // note cannot.
            _logger.LogInformation(
                "Observation stored without a vector ({Outcome}); it will match on text until re-embedded.",
                result.Outcome);

            return result.Outcome;
        }

        await UpsertAsync(
                ownerId,
                new Vector(result.Vector!.ToArray().AsMemory()),
                hash,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        return EmbeddingOutcome.Succeeded;
    }

    private async Task UpsertAsync(
        string ownerId,
        Vector vector,
        string hash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Local first, then the store. Checking only what this context is already tracking would look right on
        // the write path -- where the owner is always new -- and turn a re-embed into a primary key violation
        // the moment anything embeds an observation twice. The extra round trip is paid once per write and is
        // nothing beside the HTTP call it sits next to.
        EmbeddingRecord? existing =
            _database.Embeddings.Local.FirstOrDefault(e =>
                e.OwnerKind == EmbeddingOwnerKind.Observation
                && e.OwnerId == ownerId
                && e.Model == _provider.Model)
            ?? await _database.Embeddings
                .FirstOrDefaultAsync(
                    e => e.OwnerKind == EmbeddingOwnerKind.Observation
                        && e.OwnerId == ownerId
                        && e.Model == _provider.Model,
                    cancellationToken)
                .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Embedding = vector;
            existing.ContentHash = hash;
            existing.RecordedAt = now;
            return;
        }

        _database.Embeddings.Add(new EmbeddingRecord
        {
            OwnerKind = EmbeddingOwnerKind.Observation,
            OwnerId = ownerId,
            Model = _provider.Model,
            Dimensions = _provider.Dimensions,
            Embedding = vector,
            ContentHash = hash,
            RecordedAt = now,
        });
    }

    /// <summary>
    /// A one-line account of what happened, for the tool result.
    /// </summary>
    /// <param name="outcome">What happened.</param>
    /// <returns>The explanation, or <see langword="null"/> when a vector was stored.</returns>
    public static string? Explain(EmbeddingOutcome outcome) => outcome switch
    {
        EmbeddingOutcome.Succeeded => null,
        EmbeddingOutcome.NotConfigured =>
            "Stored without a vector: embeddings are not configured. It will match on text.",
        EmbeddingOutcome.RateLimited =>
            "Stored without a vector: the embedding provider rate-limited the call. It will match on text "
            + "until re-embedded.",
        EmbeddingOutcome.Rejected =>
            "Stored without a vector: the embedding provider rejected the credential. Check Embeddings__ApiKey "
            + "— retrying will not help. It will match on text until re-embedded.",
        EmbeddingOutcome.Malformed =>
            "Stored without a vector: the embedding provider returned an unusable response. It will match on "
            + "text until re-embedded.",
        // Deliberately does NOT say "could not be reached". This bucket also holds statuses where the
        // provider answered and refused, and sending an operator to check the network when the service
        // returned 400 is the same overclaiming this repository keeps having to undo.
        _ =>
            "Stored without a vector: the embedding provider did not return one. It will match on text until "
            + "re-embedded.",
    };
}
