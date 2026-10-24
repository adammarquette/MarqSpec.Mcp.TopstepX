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
            // DO block, or in dynamic SQL. It also cannot be unconditional: ADR-0004 leaves Trades a plain
            // table when Timescale is absent, and plain PG rejects the timescaledb reloption.
            //
            // The procedure is the equivalent of that DO block. CALL is its own command, outside EF's
            // transaction, so the COMMIT inside it is not a transaction block. An invalid index is dropped
            // first: an interrupted per-chunk build leaves one, and IF NOT EXISTS would then skip it.
            // Npgsql's migration lock is LOCK TABLE on the history table and dies when EF commits before
            // this statement, so the build takes its own session advisory lock. The model and the snapshot
            // still declare the index, so the next migration does not emit a second CreateIndex.
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
                    PERFORM pg_advisory_lock(123207681, 190601);

                    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
                        per_chunk := EXISTS (
                            SELECT 1
                            FROM timescaledb_information.hypertables
                            WHERE hypertable_schema = 'public'
                              AND hypertable_name = 'Trades');
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
                        -- Leaves the transaction CALL opened. The next statement has to be static:
                        -- EXECUTE would be a transaction block again.
                        COMMIT;
                        CREATE INDEX IF NOT EXISTS "IX_Trades_Instrument_TradeTimeUtc"
                            ON "Trades" ("Instrument", "TradeTimeUtc")
                            WITH (timescaledb.transaction_per_chunk);
                    ELSE
                        CREATE INDEX IF NOT EXISTS "IX_Trades_Instrument_TradeTimeUtc"
                            ON "Trades" ("Instrument", "TradeTimeUtc");
                        COMMIT;
                    END IF;

                    PERFORM pg_advisory_unlock(123207681, 190601);
                END
                $body$;
                """,
                suppressTransaction: true);

            // Its own command. A second statement in the same string is one transaction block, and the
            // per-chunk form refuses that.
            migrationBuilder.Sql(
                "CALL marqspec_build_ix_trades_instrument_time();",
                suppressTransaction: true);

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
