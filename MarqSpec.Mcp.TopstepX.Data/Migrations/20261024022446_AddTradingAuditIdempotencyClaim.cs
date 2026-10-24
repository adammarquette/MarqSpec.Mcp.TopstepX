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
