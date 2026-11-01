using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LatestQuotes",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Bid = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Ask = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Last = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    QuoteTimeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LatestQuotes", x => new { x.Venue, x.ContractId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_LatestQuotes_Instrument",
                table: "LatestQuotes",
                column: "Instrument");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LatestQuotes");
        }
    }
}
