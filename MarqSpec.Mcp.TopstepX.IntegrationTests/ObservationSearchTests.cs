using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Embeddings;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Semantic search, and the text path it falls back to.
/// </summary>
/// <remarks>
/// Integration rather than unit for the same reason as the writer's tests: <c>EmbeddingRecord</c> is excluded
/// from the model off Npgsql, and cosine ordering is something <i>Postgres</i> does. A fake store would sort
/// in C# and prove nothing about the query that runs.
/// </remarks>
/// <param name="fixture">The shared container.</param>
[Collection(SchemaCollection.Name)]
public sealed class ObservationSearchTests(SchemaFixture fixture)
{
    /// <summary>
    /// A private instrument per test, used as both the seed symbol and the search filter.
    /// </summary>
    /// <remarks>
    /// Tests share one container, and <b>the vector path has no text filter</b> — it compares against every
    /// embedding in the table. Tagging the text is therefore not isolation; only the symbol filter is. That
    /// this was the only way to isolate them is itself worth knowing: a semantic search is global unless
    /// something scopes it.
    /// </remarks>
    private static string Scope() => "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    // ── The payoff, and the acceptance criterion ─────────────────────────────────────────────────────

    [Fact]
    public async Task SemanticSearchFindsWhatSubstringMatchingMisses()
    {
        // The issue's stated payoff: "early session losses" should find "chopped up in the first hour". Those
        // two share no word at all, which is exactly why substring matching cannot do it -- and the second
        // half of this test proves that, so the first half is not merely a query that would have worked
        // either way.
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"early session losses {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();

        ObservationRecord wanted = ObservationSeed.Add(
            database, $"chopped up in the first hour again {scope}", 5, provider.Model, scope);
        ObservationSeed.Add(database, $"clean trend day from the open {scope}", 150, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome semantic = await ObservationSeed.Service(database, provider)
            .SearchAsync($"early session losses {scope}", scope, 10, CancellationToken.None);

        semantic.Mode.Should().Be(ObservationSearchMode.Semantic);
        semantic.Matches.Should().NotBeEmpty();
        semantic.Matches[0].Observation.Id.Should().Be(wanted.Id);

        // And the control: the same query, answered by substring matching, finds nothing.
        ObservationSearchOutcome text = await ObservationSeed
            .Service(database, provider, available: false)
            .SearchAsync($"early session losses {scope}", scope, 10, CancellationToken.None);

        text.Mode.Should().Be(ObservationSearchMode.Text);
        text.Matches.Should().BeEmpty("no observation contains that substring — which is the whole point");
    }

    [Fact]
    public async Task MatchesComeBackBestFirst_WithAScore()
    {
        // Order and score together. An agent handed an unordered list, or a list with no scores, cannot tell a
        // strong match from the least-bad of a weak set and will act on both the same way.
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"query {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationRecord near = ObservationSeed.Add(database, $"near {scope}", 10, provider.Model, scope);
        ObservationRecord middle = ObservationSeed.Add(database, $"middle {scope}", 60, provider.Model, scope);
        ObservationRecord far = ObservationSeed.Add(database, $"far {scope}", 120, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 10, CancellationToken.None);

        result.Matches.Select(m => m.Observation.Id)
            .Should().ContainInOrder(near.Id, middle.Id, far.Id);

        // cos(10°), cos(60°), cos(120°) -- the scores are the geometry, not an opaque ranking number.
        result.Matches[0].Similarity.Should().BeApproximately(Math.Cos(10 * Math.PI / 180), 0.001);
        result.Matches[1].Similarity.Should().BeApproximately(0.5, 0.001);
        result.Matches[2].Similarity.Should().BeApproximately(-0.5, 0.001);
    }

    [Fact]
    public async Task TheQueryIsEmbeddedAsAQuery_NotADocument()
    {
        // search_query, not search_document. They embed into different regions; using the storage type here
        // returns well-formed vectors that retrieve measurably worse, with nothing to say why (ADR-0009).
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"query {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationSeed.Add(database, $"a note {scope}", 0, provider.Model, scope);
        await database.SaveChangesAsync();

        await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 10, CancellationToken.None);

        provider.Purposes.Should().ContainSingle().Which.Should().Be(EmbeddingPurpose.Query);
    }

    // ── The fallback is a path, not an error ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutEmbeddings_TheSameCallAnswersByText_AndSaysSo()
    {
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationRecord match = ObservationSeed.Add(
            database, $"chopped up in the first hour {scope}", null, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed
            .Service(database, provider, available: false)
            .SearchAsync($"first hour {scope}", scope, 10, CancellationToken.None);

        result.Mode.Should().Be(ObservationSearchMode.Text);
        result.Reason.Should().NotBeNullOrWhiteSpace("an empty result is ambiguous without it");
        result.Matches.Should().ContainSingle().Which.Observation.Id.Should().Be(match.Id);
        result.Matches[0].Similarity.Should().BeNull("substring matching produces no score");
        provider.Calls.Should().Be(0, "an unavailable provider must not be called");
    }

    [Theory]
    [InlineData(EmbeddingOutcome.RateLimited)]
    [InlineData(EmbeddingOutcome.Unavailable)]
    [InlineData(EmbeddingOutcome.Malformed)]
    public async Task WhenTheQueryCannotBeEmbedded_ItAnswersByTextRatherThanThrowing(
        EmbeddingOutcome failure)
    {
        // A busy vendor must not become a broken tool. The question still gets an answer, less precisely, and
        // the reason says which.
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new() { ForcedOutcome = failure };

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationRecord match = ObservationSeed.Add(
            database, $"a note worth finding {scope}", 0, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed.Service(database, provider)
            .SearchAsync($"worth finding {scope}", scope, 10, CancellationToken.None);

        result.Mode.Should().Be(ObservationSearchMode.Text);
        result.Reason.Should().NotBeNullOrWhiteSpace();
        result.Matches.Should().ContainSingle().Which.Observation.Id.Should().Be(match.Id);
    }

    [Fact]
    public async Task ABlankQueryListsByRecency()
    {
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationSeed.Add(database, $"one {scope}", 0, provider.Model, scope);
        ObservationSeed.Add(database, $"two {scope}", 0, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed.Service(database, provider)
            .SearchAsync("   ", scope, 10, CancellationToken.None);

        result.Mode.Should().Be(ObservationSearchMode.Text);
        result.Matches.Should().HaveCountGreaterThanOrEqualTo(2);
        provider.Calls.Should().Be(0, "there is nothing to embed and no reason to pay for it");
    }

    // ── What the vector path cannot see ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObservationsWithNoVectorAreCounted_NotSilentlyOmitted()
    {
        // gh#46 tells an author whose embedding call failed that the note "will match on text until
        // re-embedded". Semantic search cannot keep that promise -- the row has no vector to compare. Saying
        // how many are in that state is what stops a thin result being read as a thin corpus.
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"query {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationSeed.Add(database, $"embedded {scope}", 0, provider.Model, scope);
        ObservationSeed.Add(database, $"not embedded {scope}", null, provider.Model, scope);
        ObservationSeed.Add(database, $"also not embedded {scope}", null, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 10, CancellationToken.None);

        result.Mode.Should().Be(ObservationSearchMode.Semantic);
        result.Matches.Should().ContainSingle();
        result.UnsearchableCount.Should().Be(2);
    }

    [Fact]
    public async Task AFullPageDoesNotPayForTheUnsearchableCount()
    {
        // The count is a correlated NOT EXISTS over the whole corpus in scope, with a per-row uuid-to-text
        // cast no index can serve. Paying it when the caller already has every result they asked for buys
        // them nothing -- they are not missing anything they requested.
        //
        // Null, NOT zero. Zero is an answer and this is the absence of one; reporting zero here would tell a
        // caller "nothing is missing" on the strength of never having looked.
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"query {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();
        for (int i = 0; i < 4; i++)
        {
            ObservationSeed.Add(database, $"embedded {i} {scope}", i, provider.Model, scope);
        }

        ObservationSeed.Add(database, $"no vector {scope}", null, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome full = await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 2, CancellationToken.None);

        full.Matches.Should().HaveCount(2);
        full.UnsearchableCount.Should().BeNull("the page was full, so the answer would change nothing");

        // Ask for more than exist and the page comes back short -- now it matters, so now it is counted.
        ObservationSearchOutcome short_ = await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 20, CancellationToken.None);

        short_.Matches.Should().HaveCount(4);
        short_.UnsearchableCount.Should().Be(1, "a short page is exactly when the gap explains the shortfall");
    }

    [Fact]
    public async Task TheTextPathReportsNothingUnsearchable()
    {
        // Not a shortcut -- it reads Observations directly, so every row takes part whether or not it has a
        // vector. Reporting a non-zero count here would send a caller chasing a gap that is not there.
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationSeed.Add(database, $"no vector {scope}", null, provider.Model, scope);
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed
            .Service(database, provider, available: false)
            .SearchAsync(scope, scope, 10, CancellationToken.None);

        result.UnsearchableCount.Should().Be(0, "this path read every row, so the answer is none rather than unknown");
        result.Matches.Should().ContainSingle();
    }

    // ── Scope and cap ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSymbolFilterHoldsOnTheVectorPath()
    {
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"query {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();
        ObservationRecord es = ObservationSeed.Add(database, $"es note {scope}", 5, provider.Model, scope);

        // Nearer the query than the ES note, so a filter that does not hold would put it first.
        ObservationSeed.Add(database, $"nq note {scope}", 0, provider.Model, "NQ");
        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 10, CancellationToken.None);

        result.Matches.Should().ContainSingle().Which.Observation.Id.Should().Be(es.Id);
    }

    [Fact]
    public async Task TheLimitIsHonoured()
    {
        string scope = Scope();
        ScriptedEmbeddingProvider provider = new();
        provider.At($"query {scope}", 0);

        await using TopstepXDbContext database = fixture.CreateContext();
        for (int i = 0; i < 12; i++)
        {
            ObservationSeed.Add(database, $"note {i} {scope}", i, provider.Model, scope);
        }

        await database.SaveChangesAsync();

        ObservationSearchOutcome result = await ObservationSeed.Service(database, provider)
            .SearchAsync($"query {scope}", scope, 5, CancellationToken.None);

        result.Matches.Should().HaveCount(5);
    }
}
