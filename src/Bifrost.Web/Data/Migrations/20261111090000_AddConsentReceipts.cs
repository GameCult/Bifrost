using System;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddMindKeys: no dotnet-ef on the authoring host; the snapshot was edited to match.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261111090000_AddConsentReceipts")]
    public partial class AddConsentReceipts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsentReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    NoticeVersion = table.Column<int>(type: "integer", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WithdrawnAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentReceipts", x => x.Id);
                    table.ForeignKey("FK_ConsentReceipts_UserAccounts_UserAccountId", x => x.UserAccountId, "UserAccounts", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_ConsentReceipts_UserAccountId_NoticeVersion", table: "ConsentReceipts", columns: new[] { "UserAccountId", "NoticeVersion" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ConsentReceipts");
        }
    }
}
