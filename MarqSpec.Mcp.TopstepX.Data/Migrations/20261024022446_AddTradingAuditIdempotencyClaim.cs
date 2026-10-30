using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingAuditIdempotencyClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OrderId",
                table: "TradingAuditLog",
                type: "bigint",
                nullable: true);

            // Existing duplicate Unknown claims would make the unique index fail. The table rejects UPDATE,
            // so the repair disables that trigger for this statement only, keeps the earliest Unknown row,
            // and reclassifies the rest to Attempted at count 0. They leave the partial index, they are
            // not a cap charge, and a replay still prefers the kept Unknown row.
            migrationBuilder.Sql(
                """
                ALTER TABLE "TradingAuditLog" DISABLE TRIGGER trading_audit_log_reject_update;

                UPDATE "TradingAuditLog" AS extra
                SET "Outcome" = 'Attempted',
                    "ContractCount" = 0,
                    "VenueErrorCode" = 'duplicate_unknown_claim'
                WHERE extra."Outcome" = 'Unknown'
                  AND extra."IdempotencyKey" <> ''
                  AND extra."Id" <> (
                      SELECT MIN(kept."Id")
                      FROM "TradingAuditLog" AS kept
                      WHERE kept."Outcome" = 'Unknown'
                        AND kept."IdempotencyKey" <> ''
                        AND kept."AccountId" = extra."AccountId"
                        AND kept."Tool" = extra."Tool"
                        AND kept."IdempotencyKey" = extra."IdempotencyKey"
                  );

                ALTER TABLE "TradingAuditLog" ENABLE TRIGGER trading_audit_log_reject_update;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TradingAuditLog_AccountId_Tool_IdempotencyKey_Unknown",
                table: "TradingAuditLog",
                columns: new[] { "AccountId", "Tool", "IdempotencyKey" },
                unique: true,
                filter: "\"Outcome\" = 'Unknown' AND \"IdempotencyKey\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingAuditLog_AccountId_Tool_IdempotencyKey_Unknown",
                table: "TradingAuditLog");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "TradingAuditLog");
        }
    }
}
