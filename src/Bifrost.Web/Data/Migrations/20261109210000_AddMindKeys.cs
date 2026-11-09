using System;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddSealedBallotMotions: no dotnet-ef on the authoring host; the snapshot was edited to match.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261109210000_AddMindKeys")]
    public partial class AddMindKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MindKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonaId = table.Column<string>(type: "text", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    RegisteredByUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_MindKeys", x => x.Id));
            migrationBuilder.CreateIndex(name: "IX_MindKeys_PersonaId_PublicKey", table: "MindKeys", columns: new[] { "PersonaId", "PublicKey" }, unique: true);

            migrationBuilder.CreateTable(
                name: "MindNonces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonaId = table.Column<string>(type: "text", nullable: false),
                    Nonce = table.Column<string>(type: "text", nullable: false),
                    SeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_MindNonces", x => x.Id));
            migrationBuilder.CreateIndex(name: "IX_MindNonces_PersonaId_Nonce", table: "MindNonces", columns: new[] { "PersonaId", "Nonce" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MindNonces");
            migrationBuilder.DropTable(name: "MindKeys");
        }
    }
}
