using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A provider that answers from a script, so a test can decide what "similar" means.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot prove.</b> No key and no network, so nothing here says anything about whether
/// Cohere's model is any good at understanding a trading note. What it does prove is the part this repository
/// owns: that the query is embedded as a <i>query</i>, that results come back ordered by cosine similarity,
/// that the filter and the cap hold, and that every failure degrades to text instead of throwing.
/// </para>
/// <para>
/// Model quality is Cohere's problem and is not testable without spending money on every CI run. The plumbing
/// around it is ours, and it is where the mistakes that return a plausible wrong answer live.
/// </para>
/// </remarks>
public sealed class ScriptedEmbeddingProvider : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _script = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a vector was asked for.</summary>
    public int Calls { get; private set; }

    /// <summary>What purpose each call stated, in order.</summary>
    public List<EmbeddingPurpose> Purposes { get; } = [];

    /// <summary>Forced on every call once set, so a test can simulate a rate limit or an outage.</summary>
    public EmbeddingOutcome? ForcedOutcome { get; set; }

    /// <summary>Files vectors under a different model, for a fixture seeded under one.</summary>
    public string? ModelOverride { get; set; }

    /// <inheritdoc />
    public string Model => ModelOverride ?? "scripted-v1";

    /// <inheritdoc />
    public int Dimensions => TopstepXDbContext.EmbeddingDimensions;

    /// <summary>
    /// Places text at a point on the unit circle, embedded in the first two dimensions.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="degrees">Its angle. Texts close in angle are close in cosine similarity.</param>
    /// <returns>This, for chaining.</returns>
    /// <remarks>
    /// An angle rather than hand-written coordinates because cosine similarity <i>is</i> the cosine of the
    /// angle between two vectors — so a test can say "these two are 10° apart, that one is 170° away" and the
    /// expected ordering is arithmetic rather than a guess.
    /// </remarks>
    public ScriptedEmbeddingProvider At(string text, double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        float[] vector = new float[Dimensions];
        vector[0] = (float)Math.Cos(radians);
        vector[1] = (float)Math.Sin(radians);
        _script[text.Trim()] = vector;
        return this;
    }

    /// <inheritdoc />
    public Task<EmbeddingResult> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (purpose == EmbeddingPurpose.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "A purpose must be stated.");
        }

        Calls++;
        Purposes.Add(purpose);

        if (ForcedOutcome is { } forced && forced != EmbeddingOutcome.Succeeded)
        {
            return Task.FromResult(new EmbeddingResult(forced, null, Model, 0, TimeSpan.Zero));
        }

        if (!_script.TryGetValue(text.Trim(), out float[]? vector))
        {
            // Unscripted text is a test wiring mistake, not a runtime state. Failing loudly here beats a
            // silently arbitrary vector that makes an ordering assertion pass or fail for no reason.
            throw new InvalidOperationException(
                $"No vector is scripted for \"{text}\". Call At(text, degrees) first.");
        }

        return Task.FromResult(
            new EmbeddingResult(EmbeddingOutcome.Succeeded, vector, Model, 1, TimeSpan.Zero));
    }

    /// <summary>Builds the vector for an angle, for seeding stored embeddings directly.</summary>
    /// <param name="degrees">The angle.</param>
    /// <returns>The vector.</returns>
    public static Vector VectorAt(double degrees)
    {
        double radians = degrees * Math.PI / 180d;
        float[] vector = new float[TopstepXDbContext.EmbeddingDimensions];
        vector[0] = (float)Math.Cos(radians);
        vector[1] = (float)Math.Sin(radians);
        return new Vector(vector.AsMemory());
    }
}

/// <summary>Helpers for seeding observations and their vectors.</summary>
public static class ObservationSeed
{
    /// <summary>Adds an observation, and a vector for it at the given angle.</summary>
    /// <param name="database">The store.</param>
    /// <param name="text">The observation text.</param>
    /// <param name="degrees">Where it sits, or null to store it without a vector.</param>
    /// <param name="model">The embedding model to file the vector under.</param>
    /// <param name="symbol">The instrument, if any.</param>
    /// <returns>The observation.</returns>
    public static ObservationRecord Add(
        TopstepXDbContext database,
        string text,
        double? degrees,
        string model,
        string? symbol = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        ObservationRecord record = new()
        {
            Id = Guid.NewGuid(),
            Instrument = symbol,
            Kind = "note",
            Text = text,
            Tags = [],
            RecordedAt = DateTimeOffset.UtcNow,
        };

        database.Observations.Add(record);

        if (degrees is { } angle)
        {
            database.Embeddings.Add(new EmbeddingRecord
            {
                OwnerKind = EmbeddingOwnerKind.Observation,
                OwnerId = record.Id.ToString(),
                Model = model,
                Dimensions = TopstepXDbContext.EmbeddingDimensions,
                Embedding = ScriptedEmbeddingProvider.VectorAt(angle),
                ContentHash = EmbeddingWriter.HashOf(record.Text),
                RecordedAt = record.RecordedAt,
            });
        }

        return record;
    }

    /// <summary>Builds a search service over a context and a provider.</summary>
    /// <param name="database">The store.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="available">Whether embeddings are available.</param>
    /// <returns>The service.</returns>
    public static ObservationSearchService Service(
        TopstepXDbContext database,
        IEmbeddingProvider provider,
        bool available = true)
    {
        EmbeddingAvailabilityHolder holder = new();
        holder.Set(available ? EmbeddingAvailability.Available() : EmbeddingAvailability.NoApiKey());

        return new ObservationSearchService(
            database, provider, holder, NullLogger<ObservationSearchService>.Instance);
    }
}
