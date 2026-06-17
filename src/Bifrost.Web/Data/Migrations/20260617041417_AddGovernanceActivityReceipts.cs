using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceActivityReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GovernanceActivityReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CommentId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DispatchRequestId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    JurisdictionRepoName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    JurisdictionAgentIdentity = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ActivityKind = table.Column<string>(type: "text", nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ActorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    ActorUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernanceActivityReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GovernanceActivityReceipts_UserAccounts_ActorUserAccountId",
                        column: x => x.ActorUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceActivityReceipts_ActorUserAccountId",
                table: "GovernanceActivityReceipts",
                column: "ActorUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceActivityReceipts_JurisdictionRepoName_Jurisdictio~",
                table: "GovernanceActivityReceipts",
                columns: new[] { "JurisdictionRepoName", "JurisdictionAgentIdentity", "ActivityKind" });

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceActivityReceipts_OccurredAtUtc",
                table: "GovernanceActivityReceipts",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceActivityReceipts_TopicId",
                table: "GovernanceActivityReceipts",
                column: "TopicId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GovernanceActivityReceipts");
        }
    }
}
