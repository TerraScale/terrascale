using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "agent_id",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "agent_key_id",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_policies",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    allowed_scopes = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_policies", x => x.project_id);
                    table.ForeignKey(
                        name: "FK_agent_policies_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    owner_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    allowed_scopes = table.Column<string>(type: "TEXT", nullable: false),
                    allow_self_subject = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    owner_auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    owner_auth_stamp = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => new { x.project_id, x.id });
                    table.CheckConstraint("ck_agents_expiry", "expires_at IS NULL OR expires_at > created_at");
                    table.CheckConstraint("ck_agents_status", "status IN ('active', 'revoked') AND (status = 'revoked') = (revoked_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_agents_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agents_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_keys",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    kid = table.Column<string>(type: "TEXT", nullable: false),
                    agent_id = table.Column<string>(type: "TEXT", nullable: false),
                    public_jwk = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    thumbprint = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_keys", x => new { x.project_id, x.kid });
                    table.ForeignKey(
                        name: "FK_agent_keys_agents_project_id_agent_id",
                        columns: x => new { x.project_id, x.agent_id },
                        principalTable: "agents",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_keys_project_id_agent_id",
                table: "agent_keys",
                columns: new[] { "project_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_keys_project_id_thumbprint",
                table: "agent_keys",
                columns: new[] { "project_id", "thumbprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_owner_user_id",
                table: "agents",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_agents_project_id_owner_user_id",
                table: "agents",
                columns: new[] { "project_id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_keys");

            migrationBuilder.DropTable(
                name: "agent_policies");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropColumn(
                name: "agent_id",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "agent_key_id",
                table: "webhook_outbox");
        }
    }
}
