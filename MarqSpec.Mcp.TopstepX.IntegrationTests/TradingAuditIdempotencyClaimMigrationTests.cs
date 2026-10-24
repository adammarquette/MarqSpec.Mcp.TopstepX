using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Duplicate Unknown claims that predate the unique index are reclassified before the index is created
/// (gh#750).
/// </summary>
[Collection(SchemaCollection.Name)]
public sealed class TradingAuditIdempotencyClaimMigrationTests(SchemaFixture fixture)
{
    [Fact]
    public async Task AddTradingAuditIdempotencyClaim_ReclassifiesDuplicateUnknownRows_BeforeTheUniqueIndex()
    {
        await using TopstepXDbContext database =
            await fixture.CreateEmptyDatabaseAsync("audit_dedupe_" + Guid.NewGuid().ToString("N"));
        IMigrator migrator = database.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261018142658_AddTradingAuditContractCount");

        const string hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        await database.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "TradingAuditLog"
                ("AttemptedAt","AccountId","ParsedStage","Tool","IdempotencyKey","RequestHash","DryRun","ContractCount","Outcome")
            VALUES
                (NOW(), 7, 'Practice', 'place_order', 'dup-key', {0}, false, 0, 'Unknown'),
                (NOW(), 7, 'Practice', 'place_order', 'dup-key', {0}, false, 0, 'Unknown'),
                (NOW(), 7, 'Practice', 'place_order', 'solo-key', {0}, false, 0, 'Unknown');
            """,
            hash);

        await migrator.MigrateAsync();

        long unknownDuplicates = await ScalarAsync(
            database,
            """
            SELECT COUNT(*) FROM "TradingAuditLog"
            WHERE "AccountId" = 7 AND "Tool" = 'place_order' AND "IdempotencyKey" = 'dup-key' AND "Outcome" = 'Unknown';
            """);
        long reclassified = await ScalarAsync(
            database,
            """
            SELECT COUNT(*) FROM "TradingAuditLog"
            WHERE "AccountId" = 7 AND "Tool" = 'place_order' AND "IdempotencyKey" = 'dup-key'
              AND "Outcome" = 'Attempted' AND "ContractCount" = 0 AND "VenueErrorCode" = 'duplicate_unknown_claim';
            """);
        long solo = await ScalarAsync(
            database,
            """
            SELECT COUNT(*) FROM "TradingAuditLog"
            WHERE "AccountId" = 7 AND "IdempotencyKey" = 'solo-key' AND "Outcome" = 'Unknown';
            """);

        unknownDuplicates.Should().Be(1);
        reclassified.Should().Be(1);
        solo.Should().Be(1);
    }

    private static async Task<long> ScalarAsync(TopstepXDbContext database, string sql)
    {
        System.Data.Common.DbConnection connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
