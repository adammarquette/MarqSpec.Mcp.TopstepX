using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// What a real Timescale server does with <c>IX_Trades_Instrument_TradeTimeUtc</c> (gh#756).
/// </summary>
[Collection(SchemaCollection.Name)]
public sealed class TradeInstrumentTimeIndexTests : IAsyncLifetime
{
    private readonly SchemaFixture _fixture;
    private TopstepXDbContext _database = null!;
    private string _connectionString = null!;

    public TradeInstrumentTimeIndexTests(SchemaFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        // EF's GetConnectionString drops the password. The container string keeps it, and this database is
        // the empty one created beside the shared schema.
        string name = "ix" + Guid.NewGuid().ToString("N");
        _database = await _fixture.CreateEmptyDatabaseAsync(name);
        await _database.Database.MigrateAsync();
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }
            .ConnectionString;
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task InstrumentTimeIndex_WhenAChunkExistsBeforeTheMigration_IsBuiltPerChunk()
    {
        // The row lands before the index migration. A chunk Timescale creates afterwards is indexed as a
        // side effect of creating the chunk, which a build that never took the per-chunk branch still does.
        string name = "chunk" + Guid.NewGuid().ToString("N");
        await (await _fixture.CreateEmptyDatabaseAsync(name)).DisposeAsync();

        string connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name }
            .ConnectionString;
        SentSql sent = new();
        await using TopstepXDbContext database = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(sent)
            .Options);
        await database.GetService<IMigrator>().MigrateAsync("20261024022446_AddTradingAuditIdempotencyClaim");

        await using (NpgsqlConnection seed = new(connectionString))
        {
            await seed.OpenAsync();
            await ExecuteAsync(
                seed,
                """
                INSERT INTO "Trades" ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                                      "Price", "Size", "Direction", "RecordedAt")
                VALUES ('test', 'ES', 'CON.F.US.EP.U26', TIMESTAMPTZ '2026-08-18 14:30:00+00', 1,
                        5000, 1, 1, TIMESTAMPTZ '2026-08-18 14:30:00+00');
                """);
        }

        sent.Texts.Clear();
        await database.Database.MigrateAsync();

        // NonQueryExecuted runs after the gate. A skipped per-chunk build is SELECT 1 by then, so a mutant
        // that never takes the branch, or looks up trades in lowercase, fails this assertion.
        sent.Texts.Should().Contain(text =>
            text.Contains("timescaledb.transaction_per_chunk", StringComparison.Ordinal)
            && text.Contains("ON public.\"Trades\"", StringComparison.Ordinal));

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        bool valid = await ScalarAsync<bool>(
            connection,
            """
            SELECT indisvalid
            FROM pg_index
            WHERE indexrelid = 'public."IX_Trades_Instrument_TradeTimeUtc"'::regclass;
            """);
        long chunks = await ScalarAsync<long>(connection, "SELECT count(*) FROM show_chunks('public.\"Trades\"');");
        long uncovered = await ScalarAsync<long>(
            connection,
            """
            SELECT count(*)
            FROM show_chunks('public."Trades"') AS chunk(relid)
            WHERE NOT EXISTS (
                SELECT 1
                FROM pg_index child
                WHERE child.indrelid = chunk.relid
                  AND child.indisvalid
                  AND child.indkey = (
                      SELECT parent.indkey
                      FROM pg_index parent
                      WHERE parent.indexrelid = 'public."IX_Trades_Instrument_TradeTimeUtc"'::regclass));
            """);

        valid.Should().BeTrue();
        chunks.Should().BeGreaterThan(0);
        uncovered.Should().Be(0);
    }

    [Fact]
    public async Task InstrumentTimeIndex_WhenMarkedInvalid_IsRebuiltWithANewOid()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        uint before = await IndexOidAsync(connection);
        await ExecuteAsync(
            connection,
            """
            UPDATE pg_index
            SET indisvalid = false
            WHERE indexrelid = 'public."IX_Trades_Instrument_TradeTimeUtc"'::regclass;
            """);

        await RebuildAsync(connection);

        uint after = await IndexOidAsync(connection);
        bool valid = await ScalarAsync<bool>(
            connection,
            """
            SELECT indisvalid
            FROM pg_index
            WHERE indexrelid = 'public."IX_Trades_Instrument_TradeTimeUtc"'::regclass;
            """);
        after.Should().NotBe(before);
        valid.Should().BeTrue();
    }

    [Fact(Timeout = 20_000)]
    public async Task InstrumentTimeIndex_WhenTheBuildLockIsHeld_TheSecondSessionWaits()
    {
        // Production does not set lock_timeout. pg_advisory_lock then waits. 55P03 (lock_not_available)
        // is raised only when that setting is present, which is why this test does not set it.
        await using NpgsqlConnection holder = await OpenAsync();
        await using NpgsqlConnection builder = await OpenAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(123207681, 190601);");
        Task? call = null;
        try
        {
            await ExecuteAsync(builder, UpStatements()[0]);
            call = ExecuteAsync(builder, UpStatements()[1]);

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            long waiting = 0;
            while (waiting == 0)
            {
                if (call.IsCompleted)
                {
                    await call;
                }

                call.IsCompleted.Should().BeFalse(
                    "the second session waits on the advisory lock; it does not skip the build or fail");
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("the second session never showed up waiting in pg_locks");
                }

                waiting = await ScalarAsync<long>(
                    holder,
                    """
                    SELECT count(*) FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND classid::bigint = 123207681
                      AND objid::bigint = 190601
                      AND NOT granted;
                    """);
                if (waiting == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20));
                }
            }

            await ExecuteAsync(holder, "SELECT pg_advisory_unlock(123207681, 190601);");
            await call;
            call = null;
        }
        finally
        {
            await ExecuteAsync(holder, "SELECT pg_advisory_unlock(123207681, 190601);");
            if (call is not null)
            {
                try
                {
                    await call.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // The assertion above is the result. This only stops the session staying blocked.
                }
            }

            await ExecuteAsync(builder, "SELECT pg_advisory_unlock(123207681, 190601);");
            await ExecuteAsync(builder, "DROP PROCEDURE IF EXISTS public.marqspec_build_ix_trades_instrument_time();");
        }
    }

    [Fact]
    public async Task InstrumentTimeIndex_WhenThePerChunkStatementFails_ReleasesTheBuildLock()
    {
        await _database.Database.OpenConnectionAsync();
        try
        {
            await _database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(123207681, 190601);");
            await _database.Database.ExecuteSqlRawAsync(
                "SELECT set_config('marqspec.trade_index_per_chunk', '1', false);");

            Func<Task> fail = () => _database.Database.ExecuteSqlRawAsync(
                "SELECT (1/0) WHERE 'timescaledb.transaction_per_chunk' <> '';");
            await fail.Should().ThrowAsync<PostgresException>();

            await using NpgsqlConnection other = await OpenAsync();
            bool free = await ScalarAsync<bool>(other, "SELECT pg_try_advisory_lock(123207681, 190601);");
            free.Should().BeTrue();
            if (free)
            {
                await ExecuteAsync(other, "SELECT pg_advisory_unlock(123207681, 190601);");
            }
        }
        finally
        {
            // A failed statement can leave the session aborted. Roll back first so the unlock is a
            // command the server will run, and so a leaked session lock cannot block the rest of the
            // collection: the key is cluster-wide.
            try
            {
                await _database.Database.ExecuteSqlRawAsync("ROLLBACK;");
            }
            catch (PostgresException)
            {
            }

            try
            {
                await _database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(123207681, 190601);");
            }
            catch (PostgresException)
            {
            }

            await _database.Database.CloseConnectionAsync();
        }
    }

    private async Task RebuildAsync(NpgsqlConnection connection)
    {
        foreach (string statement in UpStatements())
        {
            await ExecuteAsync(connection, statement);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return (T)value!;
    }

    private static async Task<uint> IndexOidAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            "SELECT 'public.\"IX_Trades_Instrument_TradeTimeUtc\"'::regclass::oid;",
            connection);
        return (uint)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The command text after every executing interceptor, which is the text the server received.</summary>
    private sealed class SentSql : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Texts.Add(command.CommandText);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private static IReadOnlyList<string> UpStatements()
    {
        AddTradeInstrumentTimeIndex migration = new();
        MigrationBuilder builder = new(activeProvider: null);
        MethodInfo method = typeof(AddTradeInstrumentTimeIndex).GetMethod(
            "Up",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(migration, [builder]);
        return builder.Operations.Cast<SqlOperation>().Select(operation => operation.Sql).ToList();
    }
}
