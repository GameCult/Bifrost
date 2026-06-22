using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBridgeActionTargetSurfaceName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetSurfaceName",
                table: "BridgeActions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_TargetSurface_TargetSurfaceName_Status",
                table: "BridgeActions",
                columns: new[] { "TargetSurface", "TargetSurfaceName", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BridgeActions_TargetSurface_TargetSurfaceName_Status",
                table: "BridgeActions");

            migrationBuilder.DropColumn(
                name: "TargetSurfaceName",
                table: "BridgeActions");
        }
    }
}
