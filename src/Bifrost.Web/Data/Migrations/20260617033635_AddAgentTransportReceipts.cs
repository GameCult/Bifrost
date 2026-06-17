using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentTransportReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentTransportReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    TargetRepoName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TargetRepositoryFullName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    TargetAgentIdentity = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ActivityKind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ActorUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTransportReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTransportReceipts_UserAccounts_ActorUserAccountId",
                        column: x => x.ActorUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTransportReceipts_ActorUserAccountId",
                table: "AgentTransportReceipts",
                column: "ActorUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTransportReceipts_OccurredAtUtc",
                table: "AgentTransportReceipts",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTransportReceipts_RequestId",
                table: "AgentTransportReceipts",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTransportReceipts_TargetRepoName_TargetAgentIdentity_A~",
                table: "AgentTransportReceipts",
                columns: new[] { "TargetRepoName", "TargetAgentIdentity", "ActivityKind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTransportReceipts");
        }
    }
}
