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
            // timescaledb.transaction_per_chunk builds one chunk at a time and commits between chunks, so the
            // statement cannot sit in that transaction. IF NOT EXISTS lets a retry finish when the index was
            // created and the history row was not. The model and the snapshot still declare the index, so
            // the next migration does not emit a second CreateIndex for the same columns.
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Trades_Instrument_TradeTimeUtc"
                    ON "Trades" ("Instrument", "TradeTimeUtc")
                    WITH (timescaledb.transaction_per_chunk);
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Trades_Instrument_TradeTimeUtc";
                """,
                suppressTransaction: true);
        }
    }
}
