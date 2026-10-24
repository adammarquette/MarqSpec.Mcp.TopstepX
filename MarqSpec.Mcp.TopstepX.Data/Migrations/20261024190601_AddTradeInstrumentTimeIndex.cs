using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeInstrumentTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A plain CreateIndex runs inside the migration transaction. On the Trades hypertable that lock
            // is held across every chunk, and MigrateAsync runs at startup while the recorder is inserting.
            // timescaledb.transaction_per_chunk commits per chunk, so it cannot sit in that transaction, in a
            // DO block, in dynamic SQL, or in this procedure: Timescale hangs there instead of building.
            // It also cannot be unconditional: ADR-0004 leaves Trades a plain table when Timescale is absent,
            // and plain PG rejects the timescaledb reloption.
            //
            // The procedure is the prepare half of that DO block. CALL is its own command, outside EF's
            // transaction, so the COMMIT inside it is not a transaction block. An invalid index is dropped
            // first: an interrupted per-chunk build leaves one, and IF NOT EXISTS would then skip it. When
            // Trades is not a hypertable the procedure creates the plain index. When it is, the procedure
            // marks the session and the next command is the top-level per-chunk build.
            // TradeInstrumentIndexBuildInterceptor rewrites that command to SELECT 1 unless the mark is set.
            // Npgsql's migration lock is LOCK TABLE on the history table and dies when EF commits before
            // this statement, so the build takes its own session advisory lock. The model and the snapshot
            // still declare the index, so the next migration does not emit a second CreateIndex.
            // destructive-migration: drops an invalid index left by an interrupted build. A valid index is
            // kept. The index holds no rows a rolled-back binary reads, and creating it again is the recovery.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE PROCEDURE marqspec_build_ix_trades_instrument_time()
                LANGUAGE plpgsql
                AS $body$
                DECLARE
                    per_chunk boolean := false;
                BEGIN
                    -- 123207681 is 0x07560001, distinct from the trading-cap class 0x07110024.
                    -- Session-level so it survives the commits inside the per-chunk build.
                    PERFORM pg_advisory_lock(123207681, 190601)
                    /* session */;

                    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
                        per_chunk := EXISTS (
                            SELECT 1
                            FROM timescaledb_information.hypertables
                            WHERE hypertable_schema = 'public'
                              AND hypertable_name = 'Trades'
                        )
                        /* hypertable */;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        JOIN pg_index i ON i.indexrelid = c.oid
                        WHERE n.nspname = 'public'
                          AND c.relname = 'IX_Trades_Instrument_TradeTimeUtc'
                          AND NOT i.indisvalid
                    ) THEN
                        DROP INDEX public."IX_Trades_Instrument_TradeTimeUtc";
                    END IF;

                    IF per_chunk THEN
                        -- The next migration command is the top-level per-chunk build. It reads this mark.
                        PERFORM set_config('marqspec.trade_index_per_chunk', '1', false);
                        COMMIT;
                    ELSE
                        CREATE INDEX IF NOT EXISTS "IX_Trades_Instrument_TradeTimeUtc"
                            ON "Trades" ("Instrument", "TradeTimeUtc");
                        PERFORM set_config('marqspec.trade_index_per_chunk', '0', false);
                        COMMIT;
                        PERFORM pg_advisory_unlock(123207681, 190601);
                    END IF;
                END
                $body$;
                """,
                suppressTransaction: true);

            // Its own command. A second statement in the same string is one transaction block.
            migrationBuilder.Sql(
                "CALL marqspec_build_ix_trades_instrument_time();",
                suppressTransaction: true);

            // Top-level, and only sent when the prepare step set the session mark. The interceptor rewrites
            // it otherwise, so plain Postgres never parses the reloption.
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Trades_Instrument_TradeTimeUtc"
                    ON "Trades" ("Instrument", "TradeTimeUtc")
                    WITH (timescaledb.transaction_per_chunk);
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                "SELECT pg_advisory_unlock(123207681, 190601);",
                suppressTransaction: true);

            // destructive-migration: drops the prepare procedure this migration created above. A rolled-back
            // binary never calls it, and it is not a relation the old version reads.
            migrationBuilder.Sql(
                "DROP PROCEDURE IF EXISTS marqspec_build_ix_trades_instrument_time();",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP INDEX does not rebuild anything, so it can take the migration transaction's lock.
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Trades_Instrument_TradeTimeUtc";
                """);
        }
    }
}
