using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradingAuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    ParsedStage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Tool = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VenueErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingAuditLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingAuditLog_AccountId_AttemptedAt",
                table: "TradingAuditLog",
                columns: new[] { "AccountId", "AttemptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingAuditLog_IdempotencyKey",
                table: "TradingAuditLog",
                column: "IdempotencyKey");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION trading_audit_log_reject_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                  RAISE EXCEPTION 'TradingAuditLog is append-only';
                END;
                $$;

                CREATE TRIGGER trading_audit_log_reject_update
                  BEFORE UPDATE ON "TradingAuditLog"
                  FOR EACH ROW
                  EXECUTE FUNCTION trading_audit_log_reject_mutation();

                CREATE TRIGGER trading_audit_log_reject_delete
                  BEFORE DELETE ON "TradingAuditLog"
                  FOR EACH ROW
                  EXECUTE FUNCTION trading_audit_log_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trading_audit_log_reject_delete ON "TradingAuditLog";
                DROP TRIGGER IF EXISTS trading_audit_log_reject_update ON "TradingAuditLog";
                DROP FUNCTION IF EXISTS trading_audit_log_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "TradingAuditLog");
        }
    }
}
