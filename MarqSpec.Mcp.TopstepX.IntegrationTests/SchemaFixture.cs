using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A real Postgres, on the same image the compose stack runs.
/// </summary>
/// <remarks>
/// <para>
/// The image is not incidental. <c>timescale/timescaledb-ha:pg17</c> carries both <c>timescaledb</c> and
/// <c>vector</c>, and the schema claims worth testing — the hypertables, the HNSW index, the CHECK
/// constraints, real upsert semantics — are all properties of those extensions. Testing against a different
/// Postgres would prove something about a database nobody deploys.
/// </para>
/// <para>
/// One container per collection rather than per test: startup dominates otherwise. The port is left for the
/// container to choose, because a fixed one turns two parallel runs into a confusing bind failure.
/// </para>
/// </remarks>
public sealed class SchemaFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("topstepx_mcp_test")
        .WithUsername("topstepx")
        .WithPassword("test-only")
        .Build();

    /// <summary>The connection string for the running container.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Opens a context against the container.</summary>
    /// <param name="interceptors">
    /// Interceptors to attach, for a test that needs to place another transaction's commit at a chosen point
    /// <i>between</i> two of this context's statements. An isolation-level claim is a claim about exactly that
    /// interleaving, and a test that merely runs two units of work concurrently observes it by luck or not at
    /// all.
    /// </param>
    /// <returns>The context. The caller disposes it.</returns>
    public TopstepXDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(interceptors)
            .Options);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Applying the migrations here rather than in each test is deliberate: "the migrations apply from
        // empty" is itself one of the claims, and if it fails every test in the collection should say so.
        await using TopstepXDbContext database = CreateContext();
        await database.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

/// <summary>Binds the schema tests to one shared container.</summary>
[CollectionDefinition(Name)]
public sealed class SchemaCollection : ICollectionFixture<SchemaFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "schema";
}
