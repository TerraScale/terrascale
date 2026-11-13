using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SsoIdentityLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sso_identity_links",
                columns: table => new
                {
                    domain = table.Column<string>(type: "TEXT", nullable: false),
                    issuer = table.Column<string>(type: "TEXT", nullable: false),
                    subject = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    connection_updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sso_identity_links", x => new { x.domain, x.issuer, x.subject });
                    table.ForeignKey(
                        name: "FK_sso_identity_links_memberships_organization_id_user_id",
                        columns: x => new { x.organization_id, x.user_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sso_identity_links_sso_connections_domain",
                        column: x => x.domain,
                        principalTable: "sso_connections",
                        principalColumn: "domain",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sso_identity_links_organization_id_user_id",
                table: "sso_identity_links",
                columns: new[] { "organization_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sso_identity_links");
        }
    }
}
