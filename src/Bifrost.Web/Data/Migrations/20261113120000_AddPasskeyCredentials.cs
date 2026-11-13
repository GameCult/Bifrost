using System;
using Bifrost.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddConsentReceipts: no dotnet-ef on the authoring host; the snapshot was edited to match. Additive only.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261113120000_AddPasskeyCredentials")]
    public partial class AddPasskeyCredentials : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasskeyCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    Transports = table.Column<string>(type: "text", nullable: false),
                    AaGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    Label = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StrongFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasskeyCredentials", x => x.Id);
                    table.ForeignKey("FK_PasskeyCredentials_UserAccounts_UserAccountId", x => x.UserAccountId, "UserAccounts", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_PasskeyCredentials_CredentialId", table: "PasskeyCredentials", column: "CredentialId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_PasskeyCredentials_UserAccountId", table: "PasskeyCredentials", column: "UserAccountId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PasskeyCredentials");
        }
    }
}
