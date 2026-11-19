using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAgentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents_rebuilt",
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
                        name: "FK_agents_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_keys_rebuilt",
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
                        principalTable: "agents_rebuilt",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_assertions_rebuilt",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    agent_id = table.Column<string>(type: "TEXT", nullable: false),
                    jti = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    keep_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_assertions", x => new { x.project_id, x.agent_id, x.jti });
                    table.ForeignKey(
                        name: "FK_agent_assertions_agents_project_id_agent_id",
                        columns: x => new { x.project_id, x.agent_id },
                        principalTable: "agents_rebuilt",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_enrollments_rebuilt",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    user_code_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    requested_scopes = table.Column<string>(type: "TEXT", nullable: false),
                    allow_self_subject = table.Column<bool>(type: "INTEGER", nullable: false),
                    public_jwk = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    thumbprint = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    agent_id = table.Column<string>(type: "TEXT", nullable: true),
                    kid = table.Column<string>(type: "TEXT", nullable: true),
                    last_poll_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_proof_issued_at = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_enrollments", x => new { x.project_id, x.id });
                    table.CheckConstraint("ck_agent_enrollments_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_agent_enrollments_status", "status IN ('pending', 'approved', 'denied', 'collected') AND (status = 'pending') = (user_id IS NULL) AND (status IN ('approved', 'collected')) = (agent_id IS NOT NULL AND kid IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_agent_enrollments_agents_project_id_agent_id",
                        columns: x => new { x.project_id, x.agent_id },
                        principalTable: "agents_rebuilt",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_enrollments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agent_enrollments_rebuilt");
            migrationBuilder.DropTable(name: "agent_assertions_rebuilt");
            migrationBuilder.DropTable(name: "agent_keys_rebuilt");
            migrationBuilder.DropTable(name: "agents_rebuilt");
        }
    }
}
