using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProbeDropColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // destructive-migration: not rollback-safe before v0.9.0 — Bars.ContractId has been dual-written
            // since v0.8.0 and no read path resolves it any more. THIS IS A THROWAWAY PROBE, not a real
            // migration; it exists only to show gh#529's gate go green on the runner with the marker present.
            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "Bars");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContractId",
                table: "Bars",
                nullable: true);
        }
    }
}
