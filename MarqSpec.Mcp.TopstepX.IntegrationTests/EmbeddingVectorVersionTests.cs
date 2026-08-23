using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Embeddings;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// What happens on a pgvector too old for <c>hnsw.iterative_scan</c>.
/// </summary>
/// <remarks>
/// <para>
/// A second container, on <c>pgvector/pgvector:0.7.4-pg16</c> rather than the stack's image, because the claim
/// under test is <b>about a version we do not ship</b> and cannot be made against one that satisfies it.
/// </para>
/// <para>
/// The failure this guards is not hypothetical and not gentle. On 0.7.4,
/// <c>SET LOCAL hnsw.iterative_scan = strict_order</c> raises <c>invalid configuration parameter name</c> —
/// "hnsw" is a reserved prefix — <b>and aborts the transaction</b>. Reaching that at query time turns
/// <c>search_observations</c> into an exception, in a design whose whole contract is that the text path is a
/// fallback rather than an error path. The probe has to catch it at startup, and this is what proves it does.
/// </para>
/// </remarks>
public sealed class EmbeddingVectorVersionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("pgvector/pgvector:0.7.4-pg16")
            .WithDatabase("topstepx_oldvector")
            .WithUsername("topstepx")
            .WithPassword("test-only")
            .Build();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using TopstepXDbContext database = CreateContext();
        await database.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;");
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private TopstepXDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
            .Options);

    [Fact]
    public async Task AnOldVectorExtensionDegradesToTextSearch_RatherThanBeingCalledAvailable()
    {
        await using TopstepXDbContext database = CreateContext();

        EmbeddingAvailability availability =
            await new EmbeddingAvailabilityProbe(NullLogger<EmbeddingAvailabilityProbe>.Instance)
                .ProbeAsync(
                    new EmbeddingOptions { ApiKey = "a-key", Model = "embed-v4.0" },
                    StoreAvailability.Available(),
                    database,
                    CancellationToken.None);

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Be(EmbeddingUnavailableReason.VectorExtensionTooOld);

        // The message must name the version found and the version needed. "Embeddings unavailable" sends an
        // operator to check their key; this sends them to the one thing that is actually wrong.
        availability.Explanation.Should().Contain("0.7.4").And.Contain("0.8");
    }

    [Fact]
    public async Task TheStatementThisGuardsReallyDoesFailHere()
    {
        // Pins the PREMISE, not just the guard. If a later pgvector 0.7 patch quietly started accepting the
        // GUC, the version gate above would be over-strict and this test would say so -- rather than the gate
        // silently costing semantic search on databases that could have run it.
        await using TopstepXDbContext database = CreateContext();

        Func<Task> set = () => database.Database.ExecuteSqlRawAsync(
            "SET LOCAL hnsw.iterative_scan = strict_order;");

        await set.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.SqlState == "42704" || e.MessageText.Contains("hnsw", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.8.0", true)]
    [InlineData("0.8.6", true)]
    [InlineData("0.9.1", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.7.4", false)]
    [InlineData("0.7", false)]
    [InlineData("0.10.0", true)]
    // Unparseable is TOO OLD, never new enough. What this guards aborts a transaction at query time, so the
    // safe default degrades to text rather than assuming the best and throwing later.
    [InlineData("garbage", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void VersionsAreComparedNumerically_NotLexically(string? version, bool expected) =>
        EmbeddingAvailabilityProbe.IsAtLeastMinimum(version).Should().Be(expected);
}
