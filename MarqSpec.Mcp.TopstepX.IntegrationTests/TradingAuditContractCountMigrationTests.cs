using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary><c>ContractCount</c> migration is additive and reversible (gh#711).</summary>
[Collection(SchemaCollection.Name)]
public sealed class TradingAuditContractCountMigrationTests(SchemaFixture fixture)
{
    [Fact]
    public async Task AddTradingAuditContractCount_Down_RemovesContractCountColumn()
    {
        // A private database. The collection's shared database stays at the latest migration, so this
        // reversibility walk cannot leave a later migration pending for the next test.
        await using TopstepXDbContext database =
            await fixture.CreateEmptyDatabaseAsync("audit_count_" + Guid.NewGuid().ToString("N"));
        IMigrator migrator = database.Database.GetService<IMigrator>();

        await migrator.MigrateAsync("20261018065525_AddTradingAuditLog");
        await migrator.MigrateAsync("20261018142658_AddTradingAuditContractCount");

        await database.Database.ExecuteSqlRawAsync(
            """INSERT INTO "TradingAuditLog" ("AttemptedAt","AccountId","ParsedStage","Tool","IdempotencyKey","RequestHash","DryRun","ContractCount","Outcome") VALUES (NOW(),1,'Practice','place_order','','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',false,3,'Live');""");

        await migrator.MigrateAsync("20261018065525_AddTradingAuditLog");

        Func<Task> readAfterDown = () => database.Database.ExecuteSqlRawAsync(
            """SELECT "ContractCount" FROM "TradingAuditLog" LIMIT 1;""");
        await readAfterDown.Should().ThrowAsync<Exception>();
    }
}
