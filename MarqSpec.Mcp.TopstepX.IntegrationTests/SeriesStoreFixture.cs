using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Testcontainers.PostgreSql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A real Postgres for the suites that exercise a <b>write path</b> — the store every such path now has,
/// because it no longer has a second one.
/// </summary>
/// <remarks>
/// <para>
/// These suites used to run in the unit tier against <c>Microsoft.EntityFrameworkCore.InMemory</c>, which has
/// no <c>ON CONFLICT</c>, no transactions and no snapshots. Serving it cost this repository a <i>second
/// implementation of every write</i>, in product code, that no production process ever executed — and the two
/// disagreed about what a write count meant (gh#387). The second implementation is gone, so the write paths
/// only run here.
/// </para>
/// <para>
/// <b>Separate from <see cref="SchemaFixture"/> on purpose.</b> That one holds a database the schema suites
/// share and never empty, because their claims are about the schema rather than its contents. These suites
/// each assume a store nobody else has written to — the property <c>UseInMemoryDatabase(Guid.NewGuid())</c>
/// used to give them for free — so this fixture empties the store between tests instead. xUnit runs the
/// classes of one collection <i>serially</i>, which is what makes emptying it safe.
/// </para>
/// </remarks>
public sealed class SeriesStoreFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17")
        .WithDatabase("topstepx_mcp_series")
        .WithUsername("topstepx")
        .WithPassword("test-only")
        .Build();

    private string? _truncate;

    /// <summary>The connection string for the running container.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Opens a context against the container.</summary>
    /// <param name="interceptors">Interceptors to attach.</param>
    /// <returns>The context. The caller disposes it.</returns>
    public TopstepXDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(interceptors)
            .Options);

    /// <summary>Empties every table, so the next test starts from the store it expects.</summary>
    /// <returns>The running operation.</returns>
    /// <remarks>
    /// <b>The table list is read off the model rather than written down here.</b> A list maintained by hand
    /// goes stale the first time a table is added, and it goes stale <i>silently</i>: the suite stays green
    /// while one table quietly leaks rows from one test into the next.
    /// </remarks>
    public async Task ResetAsync()
    {
        await using TopstepXDbContext database = CreateContext();

        _truncate ??= "TRUNCATE TABLE "
            + string.Join(
                ", ",
                database.Model.GetEntityTypes()
                    .Select(entity => entity.GetTableName())
                    .Where(table => table is not null)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(table => $"\"{table}\""))
            + " RESTART IDENTITY CASCADE;";

        await database.Database.ExecuteSqlRawAsync(_truncate);
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using TopstepXDbContext database = CreateContext();
        await database.Database.MigrateAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}

/// <summary>Binds the write-path suites to one shared container.</summary>
[CollectionDefinition(Name)]
public sealed class SeriesStoreCollection : ICollectionFixture<SeriesStoreFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "series-store";
}
