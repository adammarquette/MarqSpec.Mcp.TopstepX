using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using Microsoft.EntityFrameworkCore;
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
    public async Task InstrumentTimeIndex_WhenAChunkExists_IsValidOnThatChunk()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Trades" ("Venue", "Instrument", "ContractId", "TradeTimeUtc", "Sequence",
                                  "Price", "Size", "Direction", "RecordedAt")
            VALUES ('test', 'ES', 'CON.F.US.EP.U26', TIMESTAMPTZ '2026-08-18 14:30:00+00', 1,
                    5000, 1, 1, TIMESTAMPTZ '2026-08-18 14:30:00+00');
            """);

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

    [Fact]
    public async Task InstrumentTimeIndex_WhenTheBuildLockIsHeld_DoesNotStartASecondBuild()
    {
        await using NpgsqlConnection holder = await OpenAsync();
        await using NpgsqlConnection builder = await OpenAsync();
        await ExecuteAsync(holder, "SELECT pg_advisory_lock(123207681, 190601);");
        try
        {
            await ExecuteAsync(builder, UpStatements()[0]);
            await ExecuteAsync(builder, "SET lock_timeout = '500ms';");

            Func<Task> call = () => ExecuteAsync(builder, UpStatements()[1]);

            await call.Should().ThrowAsync<PostgresException>()
                .Where(exception => exception.SqlState == PostgresErrorCodes.LockNotAvailable);
        }
        finally
        {
            await ExecuteAsync(holder, "SELECT pg_advisory_unlock(123207681, 190601);");
            await ExecuteAsync(builder, "DROP PROCEDURE IF EXISTS marqspec_build_ix_trades_instrument_time();");
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
