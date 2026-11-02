using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestQuoteFieldTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAt",
                table: "LatestQuotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TradeDate",
                table: "LatestQuotes",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VolumeAt",
                table: "LatestQuotes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAt",
                table: "LatestQuotes");

            migrationBuilder.DropColumn(
                name: "TradeDate",
                table: "LatestQuotes");

            migrationBuilder.DropColumn(
                name: "VolumeAt",
                table: "LatestQuotes");
        }
    }
}
