using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingAuditContractCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContractCount",
                table: "TradingAuditLog",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractCount",
                table: "TradingAuditLog");
        }
    }
}
