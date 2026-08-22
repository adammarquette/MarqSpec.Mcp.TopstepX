using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "BarCoverage",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    RangeStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RangeEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BarCoverage", x => new { x.Venue, x.Instrument, x.ResolutionMinutes, x.RangeStart, x.RangeEnd });
                });

            migrationBuilder.CreateTable(
                name: "Bars",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bars", x => new { x.Venue, x.Instrument, x.ResolutionMinutes, x.BucketStart });
                });

            migrationBuilder.CreateTable(
                name: "Embeddings",
                columns: table => new
                {
                    OwnerKind = table.Column<int>(type: "integer", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Embeddings", x => new { x.OwnerKind, x.OwnerId, x.Model });
                    table.CheckConstraint("CK_Embeddings_OwnerKindKnown", "\"OwnerKind\" <> 0");
                });

            migrationBuilder.CreateTable(
                name: "IndicatorValues",
                columns: table => new
                {
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResolutionMinutes = table.Column<int>(type: "integer", nullable: false),
                    Indicator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndicatorValues", x => new { x.Venue, x.Instrument, x.ResolutionMinutes, x.Indicator, x.Period, x.BucketStart });
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<string[]>(type: "text[]", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Venue = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instrument = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TimeframeMinutes = table.Column<int>(type: "integer", nullable: false),
                    Bottom = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Top = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Significance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    FormedAtBucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TouchCount = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "IX_BarCoverage_Instrument_ResolutionMinutes_RangeStart_RangeEnd",
                table: "BarCoverage",
                columns: new[] { "Instrument", "ResolutionMinutes", "RangeStart", "RangeEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_Bars_Instrument_ResolutionMinutes_BucketStart",
                table: "Bars",
                columns: new[] { "Instrument", "ResolutionMinutes", "BucketStart" });

            migrationBuilder.CreateIndex(
                name: "IX_IndicatorValues_Instrument_ResolutionMinutes_Indicator_Peri~",
                table: "IndicatorValues",
                columns: new[] { "Instrument", "ResolutionMinutes", "Indicator", "Period", "BucketStart" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_Instrument_RecordedAt",
                table: "Observations",
                columns: new[] { "Instrument", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PriceLevels_Instrument_TimeframeMinutes_Active",
                table: "PriceLevels",
                columns: new[] { "Instrument", "TimeframeMinutes", "Active" });

            // ---------------------------------------------------------------------------------------------
            // Timescale hypertables -- CONDITIONAL, deliberately (ADR-0004).
            //
            // A hypertable is a PERFORMANCE property here, not a correctness one: the same rows, the same
            // queries, the same results either way. Hard-requiring the extension would make migrations fail
            // outright for a contributor without the Timescale image, which is a much worse trade than a
            // slower table. So probe, and warn rather than throw.
            //
            // (The `vector` extension above is NOT conditional, and cannot be: a vector(N) column has no
            // plain-Postgres equivalent to degrade to.)
            // ---------------------------------------------------------------------------------------------
            migrationBuilder.Sql(@"
DO $body$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        CREATE EXTENSION IF NOT EXISTS timescaledb;
        PERFORM create_hypertable('""Bars""', by_range('BucketStart'), migrate_data => true);
        PERFORM create_hypertable('""IndicatorValues""', by_range('BucketStart'), migrate_data => true);
    ELSE
        RAISE WARNING 'timescaledb is unavailable: Bars and IndicatorValues stay plain tables. Queries are '
                      'correct but unpartitioned; this is a performance difference, not a behavioural one.';
    END IF;
END
$body$;
");

            // ---------------------------------------------------------------------------------------------
            // The vector index.
            //
            // HNSW rather than IVFFlat: IVFFlat's lists are only meaningful once it has seen representative
            // data, and this table starts empty -- an IVFFlat index built at migration time is built over
            // nothing and has to be rebuilt later by someone who remembers to.
            //
            // Cosine because embedding models emit direction-normalised vectors, so magnitude carries no
            // signal and L2 would mostly measure it.
            // ---------------------------------------------------------------------------------------------
            migrationBuilder.Sql(
                @"CREATE INDEX ""IX_Embeddings_Vector_Cosine"" ON ""Embeddings"" "
                + @"USING hnsw (""Embedding"" vector_cosine_ops);");

            // Retention is DELIBERATELY absent on Bars and IndicatorValues. Both are records rather than
            // pipelines: a replay reaching for the numbers behind a past decision should find what was
            // actually used, not a window that has since aged out.
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Embeddings_Vector_Cosine"";");

            migrationBuilder.DropTable(
                name: "BarCoverage");

            migrationBuilder.DropTable(
                name: "Bars");

            migrationBuilder.DropTable(
                name: "Embeddings");

            migrationBuilder.DropTable(
                name: "IndicatorValues");

            migrationBuilder.DropTable(
                name: "Observations");

            migrationBuilder.DropTable(
                name: "PriceLevels");
        }
    }
}
