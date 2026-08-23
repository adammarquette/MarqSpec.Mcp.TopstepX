using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarqSpec.Mcp.TopstepX.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBarContractId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------------------------------------------------------------------------------------------
            // NULLABLE, AND DELIBERATELY NOT BACKFILLED.
            //
            // Bars are keyed by the venue-neutral symbol, so a quarterly roll writes the new contract's bars
            // under the same key as the old one's -- a splice with no seam recorded anywhere (gh#42,
            // ADR-0011). This column records which contract produced a bar so that seam becomes visible.
            //
            // Every row already in the table predates it. The contract was never captured at write time and
            // is not recoverable from anything the store holds: the bucket, the prices and the volume are the
            // same shape whichever quarter produced them. It could be GUESSED -- contract ids encode an
            // expiry month, and a front-month convention would map a bucket to a plausible quarter -- but a
            // guessed provenance is indistinguishable from a recorded one once written, and acting on a
            // plausible wrong number is the exact failure this column exists to prevent.
            //
            // So the existing rows stay NULL, meaning UNKNOWN. Unknown is not "the same contract as the row
            // beside it": an unrecorded run adjacent to a recorded one is reported as a roll boundary,
            // because the two are not known to be the same contract. An operator who wants provenance on old
            // history deletes those rows and refetches them.
            // ---------------------------------------------------------------------------------------------
            migrationBuilder.AddColumn<string>(
                name: "ContractId",
                table: "Bars",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "Bars");
        }
    }
}
