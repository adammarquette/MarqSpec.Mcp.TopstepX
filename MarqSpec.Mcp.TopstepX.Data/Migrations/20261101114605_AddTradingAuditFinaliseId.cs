using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingAuditFinaliseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FinaliseId",
                table: "TradingAuditLog",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradingAuditLog_FinaliseId_Outcome",
                table: "TradingAuditLog",
                columns: new[] { "FinaliseId", "Outcome" },
                unique: true,
                filter: "\"FinaliseId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingAuditLog_FinaliseId_Outcome",
                table: "TradingAuditLog");

            migrationBuilder.DropColumn(
                name: "FinaliseId",
                table: "TradingAuditLog");
        }
    }
}
