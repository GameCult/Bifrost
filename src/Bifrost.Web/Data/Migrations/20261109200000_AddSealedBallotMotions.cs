using System;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written: no dotnet-ef on the authoring host. The snapshot was edited to match; BuildTargetModel is left to the base no-op.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261109200000_AddSealedBallotMotions")]
    public partial class AddSealedBallotMotions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Candidates", table: "Motions", type: "text", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<DateTimeOffset>(name: "CommitClosesAtUtc", table: "Motions", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "Kind", table: "Motions", type: "text", nullable: false, defaultValue: "Standard");
            migrationBuilder.AddColumn<Guid>(name: "PriorMotionId", table: "Motions", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<string>(name: "ResultDigest", table: "Motions", type: "text", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<int>(name: "Round", table: "Motions", type: "integer", nullable: false, defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "SealedBallots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MotionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Commitment = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevealedCandidate = table.Column<string>(type: "text", nullable: true),
                    RevealedReason = table.Column<string>(type: "text", nullable: true),
                    Salt = table.Column<string>(type: "text", nullable: true),
                    RevealedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SealedBallots", x => x.Id);
                    table.ForeignKey("FK_SealedBallots_Motions_MotionId", x => x.MotionId, "Motions", "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_SealedBallots_UserAccounts_UserAccountId", x => x.UserAccountId, "UserAccounts", "Id", onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(name: "IX_SealedBallots_MotionId_UserAccountId", table: "SealedBallots", columns: new[] { "MotionId", "UserAccountId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_SealedBallots_UserAccountId", table: "SealedBallots", column: "UserAccountId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SealedBallots");
            foreach (var column in new[] { "Candidates", "CommitClosesAtUtc", "Kind", "PriorMotionId", "ResultDigest", "Round" })
            {
                migrationBuilder.DropColumn(name: column, table: "Motions");
            }
        }
    }
}
