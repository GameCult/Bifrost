using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConceptVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConceptSubjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Collection = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptSubjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConceptAppraisals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VoterKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Stance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VoterClass = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    NetworkTag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CastAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptAppraisals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConceptAppraisals_ConceptSubjects_SubjectId",
                        column: x => x.SubjectId,
                        principalTable: "ConceptSubjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConceptAppraisals_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConceptAppraisals_NetworkTag_CastAtUtc",
                table: "ConceptAppraisals",
                columns: new[] { "NetworkTag", "CastAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConceptAppraisals_SubjectId_VoterKey",
                table: "ConceptAppraisals",
                columns: new[] { "SubjectId", "VoterKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConceptAppraisals_UserAccountId",
                table: "ConceptAppraisals",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ConceptSubjects_Collection_Key",
                table: "ConceptSubjects",
                columns: new[] { "Collection", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConceptAppraisals");

            migrationBuilder.DropTable(
                name: "ConceptSubjects");
        }
    }
}
