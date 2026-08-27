using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropPriceLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceLevels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Bottom = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    FormedAtBucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Significance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TimeframeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Top = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TouchCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLevels", x => x.Id);
                    table.CheckConstraint("CK_PriceLevels_BottomPositive", "\"Bottom\" > 0");
                    table.CheckConstraint("CK_PriceLevels_KindKnown", "\"Kind\" <> 0");
                    table.CheckConstraint("CK_PriceLevels_TimeframePositive", "\"TimeframeMinutes\" > 0");
                    table.CheckConstraint("CK_PriceLevels_ZoneOrdered", "\"Top\" > \"Bottom\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLevels_Instrument_TimeframeMinutes_Active",
                table: "PriceLevels",
                columns: new[] { "Instrument", "TimeframeMinutes", "Active" });
        }
    }
}
