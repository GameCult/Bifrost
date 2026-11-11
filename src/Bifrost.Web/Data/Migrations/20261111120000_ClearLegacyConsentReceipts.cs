using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddMembershipSuspensionReason: data only, so no schema change and the snapshot is untouched.
    // Notice v1 was reused for new words, so any receipt written against the earlier v1 text must not count as agreement
    // to these. Every row goes; people who agreed are asked again on their next chat sign-in.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261111120000_ClearLegacyConsentReceipts")]
    public partial class ClearLegacyConsentReceipts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"ConsentReceipts\";");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted agreements cannot be restored, and must not be.
        }
    }
}
