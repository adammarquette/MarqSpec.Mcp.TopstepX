using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionIndicatorValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionIndicatorValues",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Session = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionIndicatorValues", x => new { x.Venue, x.Instrument, x.Session, x.Indicator, x.Period, x.BucketStart });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionIndicatorValues_Instrument_Session_Indicator_Period_~",
                table: "SessionIndicatorValues",
                columns: new[] { "Instrument", "Session", "Indicator", "Period", "BucketStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionIndicatorValues");
        }
    }
}
