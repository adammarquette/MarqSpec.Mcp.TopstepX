using System.Reflection;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Migrations on a server that has pgvector and does not have Timescale (ADR-0004, gh#756).
/// </summary>
/// <remarks>
/// Stock <c>postgres:17</c> cannot apply this schema: the vector column is not conditional. This image is
/// that server plus the vector extension, and nothing else. The instrument-time index must come out as a
/// plain index here. <c>WITH (timescaledb.transaction_per_chunk)</c> is what plain PG rejects.
/// </remarks>
public sealed class PlainPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("topstepx_mcp_test")
        .WithUsername("topstepx")
        .WithPassword("test-only")
        .Build();

    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Migrations_WhenTheServerHasNoTimescale_ApplyAndLeaveAPlainInstrumentTimeIndex()
    {
        await using TopstepXDbContext database = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options);
        Func<Task> migrate = () => database.Database.MigrateAsync();

        await migrate.Should().NotThrowAsync();
        (await database.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        long timescale = await ScalarAsync<long>(
            connection,
            "SELECT count(*) FROM pg_available_extensions WHERE name = 'timescaledb';");
        long vector = await ScalarAsync<long>(
            connection,
            "SELECT count(*) FROM pg_extension WHERE extname = 'vector';");
        bool valid = await ScalarAsync<bool>(
            connection,
            """
            SELECT indisvalid
            FROM pg_index
            WHERE indexrelid = 'public."IX_Trades_Instrument_TradeTimeUtc"'::regclass;
            """);
        string definition = await ScalarAsync<string>(
            connection,
            """
            SELECT indexdef FROM pg_indexes
            WHERE indexname = 'IX_Trades_Instrument_TradeTimeUtc';
            """);

        timescale.Should().Be(0);
        vector.Should().Be(1);
        valid.Should().BeTrue();
        definition.Should().Contain("\"Instrument\"");
        definition.Should().Contain("\"TradeTimeUtc\"");
        definition.Should().NotContain("timescaledb");
    }

    [Fact]
    public async Task InstrumentTimeIndex_WhenMarkedInvalidOnPlainPostgres_IsRebuilt()
    {
        await using TopstepXDbContext database = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options);
        await database.Database.MigrateAsync();

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        uint before = await ScalarAsync<uint>(
            connection,
            "SELECT 'public.\"IX_Trades_Instrument_TradeTimeUtc\"'::regclass::oid;");
        await ExecuteAsync(
            connection,
            """
            UPDATE pg_index
            SET indisvalid = false
            WHERE indexrelid = 'public."IX_Trades_Instrument_TradeTimeUtc"'::regclass;
            """);

        foreach (string statement in UpStatements())
        {
            // The per-chunk form is a later command. Plain Postgres rejects its reloption; the context
            // gate skips it on MigrateAsync, and this direct replay skips it the same way.
            if (statement.Contains("timescaledb.transaction_per_chunk", StringComparison.Ordinal))
            {
                continue;
            }

            await ExecuteAsync(connection, statement);
        }

        uint after = await ScalarAsync<uint>(
            connection,
            "SELECT 'public.\"IX_Trades_Instrument_TradeTimeUtc\"'::regclass::oid;");
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
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
