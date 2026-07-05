using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPatronSupportProjectAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "PatronSupportEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PatronSupportEvents_ProjectId",
                table: "PatronSupportEvents",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_PatronSupportEvents_Projects_ProjectId",
                table: "PatronSupportEvents",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PatronSupportEvents_Projects_ProjectId",
                table: "PatronSupportEvents");

            migrationBuilder.DropIndex(
                name: "IX_PatronSupportEvents_ProjectId",
                table: "PatronSupportEvents");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "PatronSupportEvents");
        }
    }
}
