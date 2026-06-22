using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBridgeActionIdentityProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BifrostIdentity",
                table: "BridgeActions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EpiphanyAgentIdentity",
                table: "BridgeActions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EpiphanyLaneId",
                table: "BridgeActions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EpiphanyRunId",
                table: "BridgeActions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HeimdallCapabilityReference",
                table: "BridgeActions",
                type: "character varying(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_BifrostIdentity",
                table: "BridgeActions",
                column: "BifrostIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeActions_HeimdallCapabilityReference",
                table: "BridgeActions",
                column: "HeimdallCapabilityReference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BridgeActions_BifrostIdentity",
                table: "BridgeActions");

            migrationBuilder.DropIndex(
                name: "IX_BridgeActions_HeimdallCapabilityReference",
                table: "BridgeActions");

            migrationBuilder.DropColumn(
                name: "BifrostIdentity",
                table: "BridgeActions");

            migrationBuilder.DropColumn(
                name: "EpiphanyAgentIdentity",
                table: "BridgeActions");

            migrationBuilder.DropColumn(
                name: "EpiphanyLaneId",
                table: "BridgeActions");

            migrationBuilder.DropColumn(
                name: "EpiphanyRunId",
                table: "BridgeActions");

            migrationBuilder.DropColumn(
                name: "HeimdallCapabilityReference",
                table: "BridgeActions");
        }
    }
}
