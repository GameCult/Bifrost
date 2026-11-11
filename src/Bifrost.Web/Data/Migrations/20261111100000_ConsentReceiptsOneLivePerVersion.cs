using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddConsentReceipts: no dotnet-ef on the authoring host; the snapshot was edited to match.
    // A person may withdraw and agree again to the same version, so uniqueness narrows to the live receipt.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261111100000_ConsentReceiptsOneLivePerVersion")]
    public partial class ConsentReceiptsOneLivePerVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ConsentReceipts_UserAccountId_NoticeVersion", table: "ConsentReceipts");
            migrationBuilder.CreateIndex(
                name: "IX_ConsentReceipts_UserAccountId_NoticeVersion",
                table: "ConsentReceipts",
                columns: new[] { "UserAccountId", "NoticeVersion" },
                unique: true,
                filter: "\"WithdrawnAtUtc\" IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ConsentReceipts_UserAccountId_NoticeVersion", table: "ConsentReceipts");
            migrationBuilder.CreateIndex(
                name: "IX_ConsentReceipts_UserAccountId_NoticeVersion",
                table: "ConsentReceipts",
                columns: new[] { "UserAccountId", "NoticeVersion" },
                unique: true);
        }
    }
}
