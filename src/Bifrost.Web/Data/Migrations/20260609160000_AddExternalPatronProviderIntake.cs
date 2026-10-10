using Bifrost.Web.Domain;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20260609160000_AddExternalPatronProviderIntake")]
    public partial class AddExternalPatronProviderIntake : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "PatronSupportEvents",
                type: "text",
                nullable: false,
                defaultValue: nameof(ExternalPatronProvider.Manual));

            migrationBuilder.AddColumn<string>(
                name: "ProviderEventId",
                table: "PatronSupportEvents",
                type: "character varying(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderPayerId",
                table: "PatronSupportEvents",
                type: "character varying(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubscriptionId",
                table: "PatronSupportEvents",
                type: "character varying(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PatronSupportEvents_Provider_ProviderEventId",
                table: "PatronSupportEvents",
                columns: new[] { "Provider", "ProviderEventId" },
                unique: true,
                filter: "\"ProviderEventId\" <> ''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PatronSupportEvents_Provider_ProviderEventId",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "ProviderEventId",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "ProviderPayerId",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "ProviderSubscriptionId",
                table: "PatronSupportEvents");
        }
    }
}
