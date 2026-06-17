using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDispatchRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentDispatchRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    TargetRepoName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TargetRepositoryFullName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    TargetAgentIdentity = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    LaunchMode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerProcessId = table.Column<int>(type: "integer", nullable: true),
                    ThreadId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TurnId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    LogPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ResultPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDispatchRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDispatchRuns_UserAccounts_StartedByUserAccountId",
                        column: x => x.StartedByUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDispatchRuns_RequestId",
                table: "AgentDispatchRuns",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDispatchRuns_StartedByUserAccountId",
                table: "AgentDispatchRuns",
                column: "StartedByUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDispatchRuns_Status",
                table: "AgentDispatchRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDispatchRuns_TargetRepoName_TargetAgentIdentity_Status",
                table: "AgentDispatchRuns",
                columns: new[] { "TargetRepoName", "TargetAgentIdentity", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDispatchRuns");
        }
    }
}
