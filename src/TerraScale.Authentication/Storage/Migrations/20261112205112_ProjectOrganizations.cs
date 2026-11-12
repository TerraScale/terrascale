using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "organization_id",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "organization_role",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_organizations",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    image_url = table.Column<string>(type: "TEXT", nullable: true),
                    public_metadata = table.Column<string>(type: "TEXT", nullable: false),
                    private_metadata = table.Column<string>(type: "TEXT", nullable: false),
                    max_members = table.Column<int>(type: "INTEGER", nullable: true),
                    domain_join = table.Column<string>(type: "TEXT", nullable: false),
                    members = table.Column<int>(type: "INTEGER", nullable: false),
                    admins = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_organizations", x => new { x.project_id, x.id });
                    table.CheckConstraint("ck_project_organizations_counts", "admins >= 0 AND (members = 0 OR admins >= 1) AND members >= admins AND (max_members IS NULL OR members <= max_members)");
                    table.CheckConstraint("ck_project_organizations_join", "domain_join IN ('off', 'suggest', 'auto')");
                    table.CheckConstraint("ck_project_organizations_slug", "length(slug) BETWEEN 3 AND 63 AND slug = lower(slug)");
                    table.ForeignKey(
                        name: "FK_project_organizations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_organization_dismissals",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_organization_dismissals", x => new { x.project_id, x.organization_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_project_organization_dismissals_project_organizations_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "project_organizations",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_organization_dismissals_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_organization_domains",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    domain = table.Column<string>(type: "TEXT", nullable: false),
                    verification_token = table.Column<string>(type: "TEXT", nullable: false),
                    verified_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    checked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    failed_checks = table.Column<int>(type: "INTEGER", nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_organization_domains", x => new { x.project_id, x.organization_id, x.domain });
                    table.ForeignKey(
                        name: "FK_project_organization_domains_project_organizations_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "project_organizations",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_organization_invitations",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_organization_invitations", x => new { x.project_id, x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_project_organization_invitations_project_organizations_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "project_organizations",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_organization_memberships",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_organization_memberships", x => new { x.project_id, x.organization_id, x.user_id });
                    table.CheckConstraint("ck_project_organization_memberships_role", "role IN ('admin', 'member')");
                    table.ForeignKey(
                        name: "FK_project_organization_memberships_project_organizations_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "project_organizations",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_organization_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_organization_dismissals_user_id",
                table: "project_organization_dismissals",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_organization_domains_lease_until_checked_at",
                table: "project_organization_domains",
                columns: new[] { "lease_until", "checked_at" },
                filter: "verified_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_organization_domains_project_id_domain",
                table: "project_organization_domains",
                columns: new[] { "project_id", "domain" },
                unique: true,
                filter: "verified_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_organization_invitations_token_hash",
                table: "project_organization_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_organization_memberships_project_id_user_id",
                table: "project_organization_memberships",
                columns: new[] { "project_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_organization_memberships_user_id",
                table: "project_organization_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_organizations_project_id_slug",
                table: "project_organizations",
                columns: new[] { "project_id", "slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_organization_dismissals");

            migrationBuilder.DropTable(
                name: "project_organization_domains");

            migrationBuilder.DropTable(
                name: "project_organization_invitations");

            migrationBuilder.DropTable(
                name: "project_organization_memberships");

            migrationBuilder.DropTable(
                name: "project_organizations");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "organization_role",
                table: "webhook_outbox");
        }
    }
}
