using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    [Migration("20260609143000_AddPatronSupportDerivation")]
    public partial class AddPatronSupportDerivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCurrentRecurringSupport",
                table: "PatronSupportEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PatronSupportEvents",
                type: "text",
                nullable: false,
                defaultValue: "OneTimeDonation");

            migrationBuilder.CreateIndex(
                name: "IX_PatronSupportEvents_UserAccountId_Kind_IsCurrentRecurringSupport",
                table: "PatronSupportEvents",
                columns: new[] { "UserAccountId", "Kind", "IsCurrentRecurringSupport" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PatronSupportEvents_UserAccountId_Kind_IsCurrentRecurringSupport",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "IsCurrentRecurringSupport",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PatronSupportEvents");
        }
    }
}
