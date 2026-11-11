using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like ConsentReceiptsOneLivePerVersion: no dotnet-ef on the authoring host; the snapshot was edited to match.
    // Reasons already written to AuditEvent.Detail stay there as history; new ones live only in this admin-read column.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261111110000_AddMembershipSuspensionReason")]
    public partial class AddMembershipSuspensionReason : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "SuspensionReason", table: "Memberships", type: "text", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SuspensionReason", table: "Memberships");
        }
    }
}
