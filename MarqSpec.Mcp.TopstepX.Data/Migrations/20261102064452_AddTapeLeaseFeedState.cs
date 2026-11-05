using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTapeLeaseFeedState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HubConnected",
                table: "TapeLeases",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Listening",
                table: "TapeLeases",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                table: "TapeLeases",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StoppedAt",
                table: "TapeLeases",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HubConnected",
                table: "TapeLeases");

            migrationBuilder.DropColumn(
                name: "Listening",
                table: "TapeLeases");

            migrationBuilder.DropColumn(
                name: "StopReason",
                table: "TapeLeases");

            migrationBuilder.DropColumn(
                name: "StoppedAt",
                table: "TapeLeases");
        }
    }
}
