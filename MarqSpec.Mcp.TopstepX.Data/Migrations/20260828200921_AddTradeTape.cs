using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeTape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FootprintCells",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BuyVolume = table.Column<long>(type: "bigint", nullable: false),
                    SellVolume = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FootprintCells", x => new { x.Venue, x.Instrument, x.ResolutionMinutes, x.BucketStart, x.Price });
                });

            migrationBuilder.CreateTable(
                name: "TapeCoverage",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RangeStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RangeEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TapeCoverage", x => new { x.Venue, x.Instrument, x.ContractId, x.RangeStart, x.RangeEnd });
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TradeTimeUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => new { x.Venue, x.Instrument, x.ContractId, x.TradeTimeUtc, x.Sequence });
                });

            migrationBuilder.CreateIndex(
                name: "IX_FootprintCells_Instrument_ResolutionMinutes_BucketStart",
                table: "FootprintCells",
                columns: new[] { "Instrument", "ResolutionMinutes", "BucketStart" });

            migrationBuilder.CreateIndex(
                name: "IX_TapeCoverage_Instrument_ContractId_RangeStart_RangeEnd",
                table: "TapeCoverage",
                columns: new[] { "Instrument", "ContractId", "RangeStart", "RangeEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_Instrument_ContractId_TradeTimeUtc",
                table: "Trades",
                columns: new[] { "Instrument", "ContractId", "TradeTimeUtc" });

            // ---------------------------------------------------------------------------------------------
            // Timescale hypertable + compression -- CONDITIONAL, same probe as InitialSchema (ADR-0004).
            //
            // A hypertable is a PERFORMANCE property: the same rows, the same queries, the same results
            // either way. Hard-requiring the extension would make this migration fail for a contributor
            // without the Timescale image. Probe, and warn rather than throw.
            //
            // Compression, not retention. This is the store's first compression policy. Retention stays
            // deliberately absent -- this tape is a record, and a replay must find what was actually used.
            // SchemaTests.BarsAndIndicatorValues_CarryNoRetentionPolicy asserts policy_retention is empty;
            // compression is a different job type and will not trip it.
            // ---------------------------------------------------------------------------------------------
            migrationBuilder.Sql(@"
DO $body$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        CREATE EXTENSION IF NOT EXISTS timescaledb;
        PERFORM create_hypertable('""Trades""', by_range('TradeTimeUtc'), migrate_data => true);
        ALTER TABLE ""Trades"" SET (
            timescaledb.compress,
            timescaledb.compress_segmentby = '""Venue"", ""Instrument"", ""ContractId""',
            timescaledb.compress_orderby = '""TradeTimeUtc"", ""Sequence""'
        );
        PERFORM add_compression_policy('""Trades""', INTERVAL '7 days');
    ELSE
        RAISE WARNING 'timescaledb is unavailable: Trades stays a plain table. Queries are '
                      'correct but unpartitioned; this is a performance difference, not a behavioural one.';
    END IF;
END
$body$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FootprintCells");

            migrationBuilder.DropTable(
                name: "TapeCoverage");

            migrationBuilder.DropTable(
                name: "Trades");
        }
    }
}
