using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAgents : Migration
    {
        private static readonly (string Table, string Columns)[] Tables =
        [
            ("agents", "project_id, id, owner_user_id, name, status, allowed_scopes, allow_self_subject, created_at, expires_at, revoked_at, last_used_at, owner_auth_epoch, owner_auth_stamp, version"),
            ("agent_keys", "project_id, kid, agent_id, public_jwk, thumbprint, created_at, revoked_at, version"),
            ("agent_assertions", "project_id, agent_id, jti, keep_until, version"),
            ("agent_enrollments", "project_id, id, user_code_hash, name, requested_scopes, allow_self_subject, public_jwk, thumbprint, status, created_at, expires_at, user_id, agent_id, kid, last_poll_at, last_proof_issued_at, version"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, projectKeys: false);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, projectKeys: true);

        private static void Rebuild(MigrationBuilder migrationBuilder, bool projectKeys)
        {
            foreach (var (table, columns) in Tables)
            {
                migrationBuilder.Sql($"CREATE TABLE {table}_rebuild ({columns});");
                migrationBuilder.Sql($"INSERT INTO {table}_rebuild ({columns}) SELECT {columns} FROM {table};");
            }

            foreach (var (table, _) in Enumerable.Reverse(Tables))
            {
                migrationBuilder.DropTable(name: table);
            }

            Create(migrationBuilder, projectKeys);
            foreach (var (table, columns) in Tables)
            {
                migrationBuilder.Sql($"INSERT INTO {table} ({columns}) SELECT {columns} FROM {table}_rebuild;");
            }

            foreach (var (table, _) in Enumerable.Reverse(Tables))
            {
                migrationBuilder.Sql($"DROP TABLE {table}_rebuild;");
            }
        }

        private static void Create(MigrationBuilder migrationBuilder, bool projectKeys)
        {
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
                    if (projectKeys)
                    {
                        table.ForeignKey(
                            name: "FK_agents_projects_project_id",
                            column: x => x.project_id,
                            principalTable: "projects",
                            principalColumn: "id",
                            onDelete: ReferentialAction.Cascade);
                    }

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

            migrationBuilder.CreateTable(
                name: "agent_assertions",
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
                        principalTable: "agents",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_enrollments",
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
                        principalTable: "agents",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    if (projectKeys)
                    {
                        table.ForeignKey(
                            name: "FK_agent_enrollments_projects_project_id",
                            column: x => x.project_id,
                            principalTable: "projects",
                            principalColumn: "id",
                            onDelete: ReferentialAction.Cascade);
                    }

                    table.ForeignKey(
                        name: "FK_agent_enrollments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_owner_user_id",
                table: "agents",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_agents_project_id_owner_user_id",
                table: "agents",
                columns: new[] { "project_id", "owner_user_id" });

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
                name: "IX_agent_assertions_keep_until",
                table: "agent_assertions",
                column: "keep_until");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollments_expires_at",
                table: "agent_enrollments",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollments_project_id_agent_id",
                table: "agent_enrollments",
                columns: new[] { "project_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollments_project_id_user_code_hash",
                table: "agent_enrollments",
                columns: new[] { "project_id", "user_code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollments_user_id",
                table: "agent_enrollments",
                column: "user_id");
        }
    }
}
