using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Embeddings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Embeddings;

/// <summary>
/// The Cohere provider, driven against a scripted transport.
/// </summary>
/// <remarks>
/// No API key and no network. What matters here is not that a happy call works — it is that <b>every failure
/// becomes an outcome rather than an exception</b>, because a rate limit must never take an exception out of a
/// retrieval call, and that the request carries the two fields ADR-0009 says decide whether the result is
/// right at all.
/// </remarks>
public sealed class CohereEmbeddingProviderTests
{
    private const string Model = "embed-v4.0";

    /// <summary>A transport that answers from a script and records what it was asked.</summary>
    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string VectorBody(int width, int tokens = 7)
    {
        // Concatenated rather than an interpolated raw string: the literal closing braces of Cohere's nested
        // JSON collide with interpolation syntax, and the workaround is less readable than this.
        string floats = string.Join(",", Enumerable.Repeat("0.1", width));
        return "{\"embeddings\":{\"float\":[[" + floats + "]]},"
            + "\"meta\":{\"billed_units\":{\"input_tokens\":"
            + tokens.ToString(CultureInfo.InvariantCulture) + "}}}";
    }

    private static (CohereEmbeddingProvider Provider, ScriptedHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        ScriptedHandler handler = new(respond);
        HttpClient http = new(handler) { BaseAddress = new Uri("https://api.cohere.com/") };

        return (
            new CohereEmbeddingProvider(
                http,
                Options.Create(new EmbeddingOptions { ApiKey = "k", Model = Model }),
                NullLogger<CohereEmbeddingProvider>.Instance),
            handler);
    }

    // ── The request has to carry two things or the answer is wrong ───────────────────────────────────

    [Fact]
    public async Task TheRequestPinsOutputDimension()
    {
        // embed-v4.0 DEFAULTS TO 1536 and the column is vector(1024). Omitting this makes every vector the
        // wrong width, and the failure arrives at the database after the call has been paid for.
        (CohereEmbeddingProvider provider, ScriptedHandler handler) =
            Build(_ => Json(HttpStatusCode.OK, VectorBody(TopstepXDbContext.EmbeddingDimensions)));

        await provider.EmbedAsync("text", EmbeddingPurpose.Document, CancellationToken.None);

        using JsonDocument sent = JsonDocument.Parse(handler.Bodies.Single());
        sent.RootElement.GetProperty("output_dimension").GetInt32()
            .Should().Be(TopstepXDbContext.EmbeddingDimensions);
    }

    [Theory]
    [InlineData(EmbeddingPurpose.Document, "search_document")]
    [InlineData(EmbeddingPurpose.Query, "search_query")]
    public async Task TheRequestSendsTheRightInputType(EmbeddingPurpose purpose, string expected)
    {
        // Not a label -- it changes the vector. Using one value for both degrades retrieval measurably while
        // returning perfectly well-formed vectors (ADR-0009).
        (CohereEmbeddingProvider provider, ScriptedHandler handler) =
            Build(_ => Json(HttpStatusCode.OK, VectorBody(TopstepXDbContext.EmbeddingDimensions)));

        await provider.EmbedAsync("text", purpose, CancellationToken.None);

        using JsonDocument sent = JsonDocument.Parse(handler.Bodies.Single());
        sent.RootElement.GetProperty("input_type").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task AnUnstatedPurposeIsRefused()
    {
        // Refused rather than defaulted: a default would be wrong half the time and never say so.
        (CohereEmbeddingProvider provider, ScriptedHandler handler) =
            Build(_ => Json(HttpStatusCode.OK, VectorBody(TopstepXDbContext.EmbeddingDimensions)));

        Func<Task> embed = () => provider.EmbedAsync("text", EmbeddingPurpose.Unknown, CancellationToken.None);

        await embed.Should().ThrowAsync<ArgumentOutOfRangeException>();
        handler.Calls.Should().Be(0, "a refused call must not reach the vendor");
    }

    // ── A good answer ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASuccessfulCallReturnsTheVectorAndItsCost()
    {
        (CohereEmbeddingProvider provider, _) =
            Build(_ => Json(HttpStatusCode.OK, VectorBody(TopstepXDbContext.EmbeddingDimensions, tokens: 42)));

        EmbeddingResult result = await provider.EmbedAsync(
            "text", EmbeddingPurpose.Document, CancellationToken.None);

        result.Outcome.Should().Be(EmbeddingOutcome.Succeeded);
        result.HasVector.Should().BeTrue();
        result.Vector!.Count.Should().Be(TopstepXDbContext.EmbeddingDimensions);
        result.Model.Should().Be(Model);
        result.BilledTokens.Should().Be(42, "an unmetered call is invisible spend on the operator's key");
    }

    // ── Every failure is an outcome, never an exception ──────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, EmbeddingOutcome.RateLimited)]
    // A refused credential is NOT Unavailable. The operator's next move is opposite -- fix the key rather
    // than wait -- and only a distinct outcome can tell them which.
    [InlineData(HttpStatusCode.Unauthorized, EmbeddingOutcome.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, EmbeddingOutcome.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, EmbeddingOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, EmbeddingOutcome.Unavailable)]
    public async Task AnErrorStatusBecomesAnOutcome(HttpStatusCode status, EmbeddingOutcome expected)
    {
        (CohereEmbeddingProvider provider, _) = Build(_ => Json(status, "{}"));

        EmbeddingResult result = await provider.EmbedAsync(
            "text", EmbeddingPurpose.Document, CancellationToken.None);

        result.Outcome.Should().Be(expected);
        result.HasVector.Should().BeFalse();
    }

    [Fact]
    public async Task ANetworkFailureBecomesAnOutcome()
    {
        (CohereEmbeddingProvider provider, _) = Build(_ => throw new HttpRequestException("no route"));

        EmbeddingResult result = await provider.EmbedAsync(
            "text", EmbeddingPurpose.Document, CancellationToken.None);

        result.Outcome.Should().Be(EmbeddingOutcome.Unavailable);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"embeddings":{"float":[]}}""")]
    [InlineData("""{"embeddings":{}}""")]
    public async Task AResponseWithNoVectorIsMalformed(string body)
    {
        (CohereEmbeddingProvider provider, _) = Build(_ => Json(HttpStatusCode.OK, body));

        EmbeddingResult result = await provider.EmbedAsync(
            "text", EmbeddingPurpose.Document, CancellationToken.None);

        result.Outcome.Should().Be(EmbeddingOutcome.Malformed);
    }

    [Fact]
    public async Task AWrongWidthVectorIsRejectedAtTheSeam()
    {
        // 1536 is embed-v4.0's DEFAULT, so this is the exact shape of forgetting output_dimension. Postgres
        // would reject it too, but only after the call was paid for -- and a constraint violation does not
        // say why.
        (CohereEmbeddingProvider provider, _) = Build(_ => Json(HttpStatusCode.OK, VectorBody(1536)));

        EmbeddingResult result = await provider.EmbedAsync(
            "text", EmbeddingPurpose.Document, CancellationToken.None);

        result.Outcome.Should().Be(EmbeddingOutcome.Malformed);
        result.HasVector.Should().BeFalse();
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        // The one thing that must NOT be swallowed. It is the caller changing their mind, not a provider
        // failure, and turning it into an outcome leaves them waiting on abandoned work.
        (CohereEmbeddingProvider provider, _) =
            Build(_ => Json(HttpStatusCode.OK, VectorBody(TopstepXDbContext.EmbeddingDimensions)));

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> embed = () => provider.EmbedAsync("text", EmbeddingPurpose.Document, cancelled.Token);

        await embed.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BlankTextIsRefusedWithoutACall()
    {
        (CohereEmbeddingProvider provider, ScriptedHandler handler) =
            Build(_ => Json(HttpStatusCode.OK, VectorBody(TopstepXDbContext.EmbeddingDimensions)));

        EmbeddingResult result = await provider.EmbedAsync(
            "   ", EmbeddingPurpose.Document, CancellationToken.None);

        result.HasVector.Should().BeFalse();
        handler.Calls.Should().Be(0, "there is nothing to embed and no reason to pay for it");
    }
}
