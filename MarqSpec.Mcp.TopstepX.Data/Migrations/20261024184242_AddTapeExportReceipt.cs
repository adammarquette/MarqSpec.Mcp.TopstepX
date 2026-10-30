using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTapeExportReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TapeExportReceipts",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    ExportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DriveFileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UploadState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TapeExportReceipts", x => new { x.Day, x.Kind, x.Format, x.Venue, x.Instrument, x.ContractId, x.ResolutionMinutes });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TapeExportReceipts_Day_UploadState",
                table: "TapeExportReceipts",
                columns: new[] { "Day", "UploadState" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TapeExportReceipts");
        }
    }
}
