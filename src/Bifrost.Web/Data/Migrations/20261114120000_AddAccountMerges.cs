using System;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Scaffolded by IMigrationsScaffolder against the previous snapshot (no dotnet-ef on the authoring host), the designer file dropped and
    // the snapshot edited by hand to match, like AddPasskeyCredentials. Additive only; Down drops what Up adds.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261114120000_AddAccountMerges")]
    public partial class AddAccountMerges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VoidedByMergeId",
                table: "Votes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedUtc",
                table: "Votes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VoidedByMergeId",
                table: "ConceptAppraisals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedUtc",
                table: "ConceptAppraisals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountMerges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SurvivorUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FoldedUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CommittedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GraceEndsUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinalizedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UndoneUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMerges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountMerges_UserAccounts_FoldedUserAccountId",
                        column: x => x.FoldedUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountMerges_UserAccounts_SurvivorUserAccountId",
                        column: x => x.SurvivorUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccountMergeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MergeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PriorUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    PriorText = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PriorAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMergeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountMergeEntries_AccountMerges_MergeId",
                        column: x => x.MergeId,
                        principalTable: "AccountMerges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMergeEntries_MergeId_Seq",
                table: "AccountMergeEntries",
                columns: new[] { "MergeId", "Seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AccountMerges_FoldedUserAccountId_Live",
                table: "AccountMerges",
                column: "FoldedUserAccountId",
                unique: true,
                filter: "\"State\" IN ('Confirmed', 'HeimdallMoved', 'Committed', 'Final', 'UndoRequested', 'HeimdallReversed')");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_FoldedUserAccountId_State",
                table: "AccountMerges",
                columns: new[] { "FoldedUserAccountId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_State_GraceEndsUtc",
                table: "AccountMerges",
                columns: new[] { "State", "GraceEndsUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_SurvivorUserAccountId_State",
                table: "AccountMerges",
                columns: new[] { "SurvivorUserAccountId", "State" });

            // The cross-role guard no single unique index can express: a live merge's survivor is never another live merge's folded
            // account (no chains), whichever order they arrive in. Advisory locks on both accounts, taken in a fixed order, serialise two
            // transactions racing over the same account, so the second one's check sees the first's committed row.
            migrationBuilder.Sql(@"
CREATE FUNCTION ""AccountMerges_refuse_chain""() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.""State"" IN ('Confirmed', 'HeimdallMoved', 'Committed', 'Final', 'UndoRequested', 'HeimdallReversed') THEN
        PERFORM pg_advisory_xact_lock(hashtextextended(LEAST(NEW.""SurvivorUserAccountId""::text, NEW.""FoldedUserAccountId""::text), 0));
        PERFORM pg_advisory_xact_lock(hashtextextended(GREATEST(NEW.""SurvivorUserAccountId""::text, NEW.""FoldedUserAccountId""::text), 0));
        IF EXISTS (
            SELECT 1 FROM ""AccountMerges"" m
            WHERE m.""Id"" <> NEW.""Id""
              AND m.""State"" IN ('Confirmed', 'HeimdallMoved', 'Committed', 'Final', 'UndoRequested', 'HeimdallReversed')
              AND (m.""SurvivorUserAccountId"" = NEW.""FoldedUserAccountId"" OR m.""FoldedUserAccountId"" = NEW.""SurvivorUserAccountId""))
        THEN
            RAISE EXCEPTION 'account merge chain refused' USING ERRCODE = '23P01';
        END IF;
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER ""AccountMerges_refuse_chain"" BEFORE INSERT OR UPDATE OF ""State"", ""SurvivorUserAccountId"", ""FoldedUserAccountId"" ON ""AccountMerges""
    FOR EACH ROW EXECUTE FUNCTION ""AccountMerges_refuse_chain""();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Voiding lives only in the new build's query filter and these columns: dropping them (or running an older build over them)
            // counts every voided vote again, and a close in that window records a wrong result for good. Refuse while any is voided;
            // see docs/account-merge.md (Rollback).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""Votes"" WHERE ""VoidedByMergeId"" IS NOT NULL)
       OR EXISTS (SELECT 1 FROM ""ConceptAppraisals"" WHERE ""VoidedByMergeId"" IS NOT NULL) THEN
        RAISE EXCEPTION 'AddAccountMerges cannot be reverted while merged votes or appraisals are voided';
    END IF;
END
$$;");

            migrationBuilder.Sql(@"DROP TRIGGER ""AccountMerges_refuse_chain"" ON ""AccountMerges""; DROP FUNCTION ""AccountMerges_refuse_chain""();");

            migrationBuilder.DropTable(
                name: "AccountMergeEntries");

            migrationBuilder.DropTable(
                name: "AccountMerges");

            migrationBuilder.DropColumn(
                name: "VoidedByMergeId",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "VoidedUtc",
                table: "Votes");

            migrationBuilder.DropColumn(
                name: "VoidedByMergeId",
                table: "ConceptAppraisals");

            migrationBuilder.DropColumn(
                name: "VoidedUtc",
                table: "ConceptAppraisals");
        }
    }
}
