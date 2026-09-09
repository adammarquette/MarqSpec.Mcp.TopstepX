using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class BarCoverageIsPerContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------------------------------------
            // EVERY EXISTING ROW IS DELETED. NOT BACKFILLED, NOT KEPT.
            //
            // These rows are CLAIMS, not measurements: each says "this range was asked for and answered
            // empty". Every one of them was stamped by the venue-front contract, over ranges the per-contract
            // fetch policy must now ask OTHER contracts about -- and the row records nothing that says which
            // contract it was. It could be guessed from the range and a front-month convention, but a guessed
            // claim is indistinguishable from a recorded one once written, and a wrong one asserts "empty" on
            // behalf of a contract nobody asked.
            //
            // Left in place, the settled ones are the hazard in its exact form. A settled row carries
            // ExpiresAt NULL -- believed FOREVER (BarCacheService.RecentEmptyTtl / SettledHistoryAge) -- so
            // "U26 had nothing for January" would answer for H26 as well, and hide real bars for good. That
            // is the repository's "an absent number served as an ordinary one" failure, with no expiry to
            // heal it. The recent ones are harmless either way: they expire within fifteen minutes whatever
            // this migration does.
            //
            // The cost of deleting is one paced page per previously-empty settled range, once, the next time
            // something reads that range -- visible in venueRequests, and stated in the data dictionary and
            // the CHANGELOG rather than left for an operator to discover.
            //
            // This runs FIRST because the AddColumn below depends on it: the column is NOT NULL with no
            // default, which Postgres accepts only on an empty table.
            // ---------------------------------------------------------------------------------------------
            migrationBuilder.Sql(@"DELETE FROM ""BarCoverage"";");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BarCoverage",
                table: "BarCoverage");

            migrationBuilder.DropIndex(
                name: "IX_BarCoverage_Instrument_ResolutionMinutes_RangeStart_RangeEnd",
                table: "BarCoverage");

            // The `defaultValue: ""` EF emits here for a new NOT NULL string column is deliberately removed.
            // A DEFAULT '' is a placeholder contract id waiting to be written -- the empty string is a
            // contract nobody has, and it compares equal to itself across a roll -- and a missing value is
            // never a default. The DELETE above is what makes dropping it possible.
            migrationBuilder.AddColumn<string>(
                name: "ContractId",
                table: "BarCoverage",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_BarCoverage",
                table: "BarCoverage",
                columns: new[] { "Venue", "Instrument", "ResolutionMinutes", "ContractId", "RangeStart", "RangeEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_BarCoverage_Instrument_ResolutionMinutes_ContractId_RangeSt~",
                table: "BarCoverage",
                columns: new[] { "Instrument", "ResolutionMinutes", "ContractId", "RangeStart", "RangeEnd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The down path empties the ledger too, and it is not optional. Two rows differing only by
            // ContractId are distinct under the six-column key and collide under the five-column one, so a
            // Down that dropped the column with rows still present could not re-add the old primary key --
            // it would fail on a duplicate, halfway through, with the key already gone. There is also nothing
            // to preserve: a per-contract claim reinterpreted as a per-venue one is the very hazard Up
            // deleted, pointing backwards.
            migrationBuilder.Sql(@"DELETE FROM ""BarCoverage"";");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BarCoverage",
                table: "BarCoverage");

            migrationBuilder.DropIndex(
                name: "IX_BarCoverage_Instrument_ResolutionMinutes_ContractId_RangeSt~",
                table: "BarCoverage");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "BarCoverage");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BarCoverage",
                table: "BarCoverage",
                columns: new[] { "Venue", "Instrument", "ResolutionMinutes", "RangeStart", "RangeEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_BarCoverage_Instrument_ResolutionMinutes_RangeStart_RangeEnd",
                table: "BarCoverage",
                columns: new[] { "Instrument", "ResolutionMinutes", "RangeStart", "RangeEnd" });
        }
    }
}
