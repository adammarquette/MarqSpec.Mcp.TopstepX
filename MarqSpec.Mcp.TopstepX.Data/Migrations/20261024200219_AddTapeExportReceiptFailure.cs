using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTapeExportReceiptFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailureStatus",
                table: "TapeExportReceipts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureType",
                table: "TapeExportReceipts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureStatus",
                table: "TapeExportReceipts");

            migrationBuilder.DropColumn(
                name: "FailureType",
                table: "TapeExportReceipts");
        }
    }
}
