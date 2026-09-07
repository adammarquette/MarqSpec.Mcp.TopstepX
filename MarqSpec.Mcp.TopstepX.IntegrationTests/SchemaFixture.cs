using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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

    /// <summary>
    /// Creates a second, empty database on the same server and opens an <b>unmigrated</b> context on it.
    /// </summary>
    /// <param name="name">
    /// The database name. Must be unique on the server; a caller-generated id is expected.
    /// </param>
    /// <returns>The context on the new database. The caller disposes it.</returns>
    /// <remarks>
    /// <para>
    /// Deliberately not the shared database. That one is already at head by the time any test runs, so a
    /// migration's <i>effect on the rows it found</i> cannot be observed there: there is nothing to migrate
    /// and nothing that predates it. A claim of the form "this migration deletes what was there" is only
    /// testable on a database that can still be walked up to the revision before it, seeded in the old shape,
    /// and then migrated the rest of the way.
    /// </para>
    /// <para>
    /// <c>CREATE DATABASE</c> cannot run inside a transaction, so it goes through a plain connection rather
    /// than the context. The container is thrown away with the collection, so the extra database is not
    /// dropped.
    /// </para>
    /// </remarks>
    public async Task<TopstepXDbContext> CreateEmptyDatabaseAsync(string name)
    {
        await using (NpgsqlConnection connection = new(ConnectionString))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{name}\";", connection);
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder builder = new(ConnectionString) { Database = name };
        return new TopstepXDbContext(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(builder.ConnectionString, npgsql => npgsql.UseVector())
            .Options);
    }

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
