using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionBars",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Session = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CloseUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    BaseBucketCount = table.Column<int>(type: "integer", nullable: false),
                    WindowCentral = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionBars", x => new { x.Venue, x.Instrument, x.Session, x.TradeDate });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionBars_Instrument_Session_CloseUtc",
                table: "SessionBars",
                columns: new[] { "Instrument", "Session", "CloseUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionBars_Venue_Instrument_Session_OpenUtc",
                table: "SessionBars",
                columns: new[] { "Venue", "Instrument", "Session", "OpenUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionBars");
        }
    }
}
