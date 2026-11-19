using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AgentEnrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    table.ForeignKey(
                        name: "FK_agent_enrollments_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_enrollments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_enrollments");
        }
    }
}
