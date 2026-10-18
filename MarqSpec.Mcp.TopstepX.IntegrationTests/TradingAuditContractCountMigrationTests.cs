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
    public void AddTradingAuditContractCount_Down_RemovesContractCountColumn()
    {
        using TopstepXDbContext database = fixture.CreateContext();
        IMigrator migrator = database.Database.GetService<IMigrator>();

        migrator.Migrate("20261018065525_AddTradingAuditLog");
        migrator.Migrate("20261018142658_AddTradingAuditContractCount");

        database.Database.ExecuteSqlRaw(
            """INSERT INTO "TradingAuditLog" ("AttemptedAt","AccountId","ParsedStage","Tool","IdempotencyKey","RequestHash","DryRun","ContractCount","Outcome") VALUES (NOW(),1,'Practice','place_order','','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',false,3,'Live');""");

        migrator.Migrate("20261018065525_AddTradingAuditLog");

        Action readAfterDown = () => database.Database.ExecuteSqlRaw(
            """SELECT "ContractCount" FROM "TradingAuditLog" LIMIT 1;""");
        readAfterDown.Should().Throw<Exception>();

        migrator.Migrate("20261018142658_AddTradingAuditContractCount");
    }
}
