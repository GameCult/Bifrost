using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    [Migration("20260608210000_AddHeimdallUserIdentity")]
    public partial class AddHeimdallUserIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_GitHubUserId",
                table: "UserAccounts");

            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_NormalizedGitHubLogin",
                table: "UserAccounts");

            migrationBuilder.AlterColumn<long>(
                name: "GitHubUserId",
                table: "UserAccounts",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "HeimdallAccountId",
                table: "UserAccounts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_GitHubUserId",
                table: "UserAccounts",
                column: "GitHubUserId",
                unique: true,
                filter: "\"GitHubUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_HeimdallAccountId",
                table: "UserAccounts",
                column: "HeimdallAccountId",
                unique: true,
                filter: "\"HeimdallAccountId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_NormalizedGitHubLogin",
                table: "UserAccounts",
                column: "NormalizedGitHubLogin",
                unique: true,
                filter: "\"NormalizedGitHubLogin\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_GitHubUserId",
                table: "UserAccounts");

            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_HeimdallAccountId",
                table: "UserAccounts");

            migrationBuilder.DropIndex(
                name: "IX_UserAccounts_NormalizedGitHubLogin",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "HeimdallAccountId",
                table: "UserAccounts");

            migrationBuilder.AlterColumn<long>(
                name: "GitHubUserId",
                table: "UserAccounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_GitHubUserId",
                table: "UserAccounts",
                column: "GitHubUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_NormalizedGitHubLogin",
                table: "UserAccounts",
                column: "NormalizedGitHubLogin",
                unique: true);
        }
    }
}
