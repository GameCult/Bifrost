using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    [Migration("20260622165000_AddBifrostNativeIdentity")]
    public partial class AddBifrostNativeIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BifrostIdentity",
                table: "UserAccounts",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedBifrostIdentity",
                table: "UserAccounts",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_NormalizedBifrostIdentity",
                table: "UserAccounts",
                column: "NormalizedBifrostIdentity",
                unique: true,
                filter: "\"NormalizedBifrostIdentity\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_NormalizedBifrostIdentity",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "BifrostIdentity",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "NormalizedBifrostIdentity",
                table: "UserAccounts");
        }
    }
}
