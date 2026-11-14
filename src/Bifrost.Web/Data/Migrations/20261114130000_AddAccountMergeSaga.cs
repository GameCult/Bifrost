using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddAccountMerges, the snapshot edited to match. Additive: the saga's columns on AccountMerges. Down is
    // roll-forward only, like AddAccountMerges: it drops the Heimdall idempotency keys and the consent record, so it refuses while
    // any merge row exists (heimdall-body:ruling:merge-roll-forward-only).
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261114130000_AddAccountMergeSaga")]
    public partial class AddAccountMergeSaga : Migration
    {
        private static readonly string[] Columns =
        [
            "HeimdallMoveId", "HeimdallReverseMoveId", "SealedProofs", "PreviewDigest", "ConfirmedByUserAccountId", "ConfirmedUtc",
            "SurvivorProofProvider", "FoldedProofProviders", "ProofFromTier", "ProofToTier", "MovedIdentityIds", "Attempts",
            "NextAttemptUtc", "LastOutcome", "UndoRequestedUtc", "ReversedUtc"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "HeimdallMoveId", table: "AccountMerges", type: "character varying(64)", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "HeimdallReverseMoveId", table: "AccountMerges", type: "character varying(64)", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "SealedProofs", table: "AccountMerges", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PreviewDigest", table: "AccountMerges", type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<Guid>(name: "ConfirmedByUserAccountId", table: "AccountMerges", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "ConfirmedUtc", table: "AccountMerges", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "SurvivorProofProvider", table: "AccountMerges", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(name: "FoldedProofProviders", table: "AccountMerges", type: "character varying(120)", maxLength: 120, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(name: "ProofFromTier", table: "AccountMerges", type: "character varying(8)", maxLength: 8, nullable: true);
            migrationBuilder.AddColumn<string>(name: "ProofToTier", table: "AccountMerges", type: "character varying(8)", maxLength: 8, nullable: true);
            migrationBuilder.AddColumn<string>(name: "MovedIdentityIds", table: "AccountMerges", type: "text", nullable: true);
            migrationBuilder.AddColumn<int>(name: "Attempts", table: "AccountMerges", type: "integer", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "NextAttemptUtc", table: "AccountMerges", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "LastOutcome", table: "AccountMerges", type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<DateTimeOffset>(name: "UndoRequestedUtc", table: "AccountMerges", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "ReversedUtc", table: "AccountMerges", type: "timestamp with time zone", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_HeimdallMoveId",
                table: "AccountMerges",
                column: "HeimdallMoveId",
                unique: true,
                filter: "\"HeimdallMoveId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_HeimdallReverseMoveId",
                table: "AccountMerges",
                column: "HeimdallReverseMoveId",
                unique: true,
                filter: "\"HeimdallReverseMoveId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""AccountMerges"") THEN
        RAISE EXCEPTION 'AddAccountMergeSaga cannot be reverted once an account merge exists: Bifrost is roll-forward only';
    END IF;
END
$$;");

            migrationBuilder.DropIndex(name: "IX_AccountMerges_HeimdallMoveId", table: "AccountMerges");
            migrationBuilder.DropIndex(name: "IX_AccountMerges_HeimdallReverseMoveId", table: "AccountMerges");
            foreach (var column in Columns)
            {
                migrationBuilder.DropColumn(name: column, table: "AccountMerges");
            }
        }
    }
}
