using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class UserOwnedApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_api_key_policies",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    allowed_scopes = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_api_key_policies", x => x.project_id);
                    table.ForeignKey(
                        name: "FK_user_api_key_policies_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_api_keys",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    prefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    secret_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_api_keys", x => new { x.project_id, x.id });
                    table.CheckConstraint("ck_user_api_keys_expiry", "expires_at IS NULL OR expires_at > created_at");
                    table.ForeignKey(
                        name: "FK_user_api_keys_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_api_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_api_keys_project_id_prefix",
                table: "user_api_keys",
                columns: new[] { "project_id", "prefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_api_keys_project_id_user_id",
                table: "user_api_keys",
                columns: new[] { "project_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_api_keys_user_id",
                table: "user_api_keys",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_api_key_policies");

            migrationBuilder.DropTable(
                name: "user_api_keys");
        }
    }
}
