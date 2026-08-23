using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Embeddings;

/// <summary>
/// Cohere's embed endpoint, behind the seam (ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// A hand-rolled <see cref="HttpClient"/> rather than a vendor SDK: one POST, a permissive dependency
/// surface, and failure mapping that is explicit here rather than inherited from a library whose idea of an
/// error is not this one's. Every non-success path becomes an <see cref="EmbeddingOutcome"/>, never an
/// exception — a rate limit must not take an exception out of a retrieval call.
/// </para>
/// <para>
/// <b>Two things must be sent on every request or the result is quietly wrong.</b> <c>output_dimension</c>,
/// because <c>embed-v4.0</c> defaults to 1536 and the column is <c>vector(1024)</c>; and <c>input_type</c>,
/// because a document and a query embed into different regions and using one for both degrades retrieval
/// while returning well-formed vectors.
/// </para>
/// </remarks>
public sealed class CohereEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>The configured client name, so the composition root and this agree by construction.</summary>
    public const string HttpClientName = "cohere";

    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<CohereEmbeddingProvider> _logger;

    /// <summary>Creates the provider.</summary>
    /// <param name="http">The configured client. Its base address and key are set at registration.</param>
    /// <param name="options">The embedding options.</param>
    /// <param name="logger">The logger.</param>
    public CohereEmbeddingProvider(
        HttpClient http,
        IOptions<EmbeddingOptions> options,
        ILogger<CohereEmbeddingProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Model => _options.Model;

    /// <inheritdoc />
    public int Dimensions => TopstepXDbContext.EmbeddingDimensions;

    /// <inheritdoc />
    public async Task<EmbeddingResult> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (purpose == EmbeddingPurpose.Unknown)
        {
            // Refused rather than defaulted. A default would be wrong half the time and never say so.
            throw new ArgumentOutOfRangeException(
                nameof(purpose),
                purpose,
                "An embedding purpose must be stated: it selects the input type, which changes the vector.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return EmbeddingResult.NotConfigured(Model) with { Outcome = EmbeddingOutcome.Malformed };
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            CohereEmbedRequest request = new(
                Model,
                [text],
                purpose == EmbeddingPurpose.Document ? "search_document" : "search_query",
                ["float"],
                Dimensions);

            using HttpResponseMessage response = await _http
                .PostAsJsonAsync("v2/embed", request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failed(MapStatus(response.StatusCode), started, response.StatusCode);
            }

            CohereEmbedResponse? body = await response.Content
                .ReadFromJsonAsync<CohereEmbedResponse>(cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<float>? vector = body?.Embeddings?.Float?.FirstOrDefault();

            if (vector is null || vector.Count == 0)
            {
                _logger.LogWarning("Cohere returned no vector; falling back to text search.");
                return Failed(EmbeddingOutcome.Malformed, started, null);
            }

            if (vector.Count != Dimensions)
            {
                // The output_dimension trap, caught here rather than at the database. Postgres would reject a
                // wrong-width value anyway, but only after the call has been made and paid for -- and this
                // says WHY, which a constraint violation does not.
                _logger.LogError(
                    "Cohere returned a {Actual}-wide vector where the column is {Expected}. Check that "
                    + "output_dimension is being sent: embed-v4.0 defaults to 1536.",
                    vector.Count,
                    Dimensions);

                return Failed(EmbeddingOutcome.Malformed, started, null);
            }

            return new EmbeddingResult(
                EmbeddingOutcome.Succeeded,
                vector,
                Model,
                body?.Meta?.BilledUnits?.InputTokens ?? 0,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller changing their mind, not a provider failure. Swallowing it would leave them waiting
            // on work they already abandoned.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // A timeout surfaces as TaskCanceledException with the caller's token NOT cancelled, which is why
            // the guard above tests the token rather than the exception type.
            _logger.LogWarning(ex, "Cohere embedding failed; falling back to text search.");
            return Failed(EmbeddingOutcome.Unavailable, started, null);
        }
    }

    private EmbeddingResult Failed(EmbeddingOutcome outcome, long started, HttpStatusCode? status)
    {
        if (status is not null)
        {
            _logger.LogWarning(
                "Cohere answered {Status}; embedding unavailable, falling back to text search.", status);
        }

        // Zero billed tokens rather than unknown: a failed call that the vendor did bill would need the
        // response body to say so, and none of these paths has a usable body.
        return new EmbeddingResult(outcome, null, Model, 0, Stopwatch.GetElapsedTime(started));
    }

    private static EmbeddingOutcome MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => EmbeddingOutcome.RateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => EmbeddingOutcome.Unavailable,
        _ => EmbeddingOutcome.Unavailable,
    };

    private sealed record CohereEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("texts")] IReadOnlyList<string> Texts,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("embedding_types")] IReadOnlyList<string> EmbeddingTypes,
        [property: JsonPropertyName("output_dimension")] int OutputDimension);

    private sealed record CohereEmbedResponse(
        [property: JsonPropertyName("embeddings")] CohereEmbeddings? Embeddings,
        [property: JsonPropertyName("meta")] CohereMeta? Meta);

    private sealed record CohereEmbeddings(
        [property: JsonPropertyName("float")] IReadOnlyList<float[]>? Float);

    private sealed record CohereMeta(
        [property: JsonPropertyName("billed_units")] CohereBilledUnits? BilledUnits);

    private sealed record CohereBilledUnits(
        [property: JsonPropertyName("input_tokens")] int InputTokens);
}
