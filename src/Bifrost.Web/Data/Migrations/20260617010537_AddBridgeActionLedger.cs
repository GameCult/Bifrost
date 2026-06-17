using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBridgeActionLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BridgeActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorKind = table.Column<string>(type: "text", nullable: false),
                    ActorUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TargetSurface = table.Column<string>(type: "text", nullable: false),
                    ActionKind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    MotionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetRepositoryFullName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    TargetLocator = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceKind = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    AuthorityReference = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    PolicyDecision = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    ReceiptUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ExternalReceiptId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    ReceiptPayload = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BridgeActions_Motions_MotionId",
                        column: x => x.MotionId,
                        principalTable: "Motions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BridgeActions_UserAccounts_ActorUserAccountId",
                        column: x => x.ActorUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BridgeActions_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_ActorKind_ActorName",
                table: "BridgeActions",
                columns: new[] { "ActorKind", "ActorName" });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_ActorUserAccountId",
                table: "BridgeActions",
                column: "ActorUserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_MotionId",
                table: "BridgeActions",
                column: "MotionId");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_SourceKind_SourceId",
                table: "BridgeActions",
                columns: new[] { "SourceKind", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_Status",
                table: "BridgeActions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_TargetSurface_ActionKind_Status",
                table: "BridgeActions",
                columns: new[] { "TargetSurface", "ActionKind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_WorkItemId",
                table: "BridgeActions",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgeActions");
        }
    }
}
