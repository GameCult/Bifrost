using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Web.Data.Migrations
{
    // Hand-written like AddAccountMergeSaga (no dotnet-ef on the authoring host), the snapshot edited to match. Additive: one table of
    // acks, the only fact a notice reader adds; the notices themselves are derived from AccountMerges. Down may drop it: it holds only acks.
    [DbContext(typeof(BifrostDbContext))]
    [Migration("20261114140000_AddAccountMergeNoticeAcks")]
    public partial class AddAccountMergeNoticeAcks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountMergeNoticeAcks",
                columns: table => new
                {
                    MergeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AckedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMergeNoticeAcks", x => new { x.MergeId, x.Kind, x.Target });
                    table.ForeignKey(
                        name: "FK_AccountMergeNoticeAcks_AccountMerges_MergeId",
                        column: x => x.MergeId,
                        principalTable: "AccountMerges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AccountMergeNoticeAcks");
        }
    }
}
