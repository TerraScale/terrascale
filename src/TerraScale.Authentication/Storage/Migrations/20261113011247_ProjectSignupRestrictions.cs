using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectSignupRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "restrictions",
                table: "project_auth_settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sign_up_mode",
                table: "project_auth_settings",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "public");

            migrationBuilder.Sql("UPDATE project_auth_settings SET sign_up_mode = CASE WHEN sign_up THEN 'public' ELSE 'restricted' END");

            migrationBuilder.DropColumn(
                name: "sign_up",
                table: "project_auth_settings");

            migrationBuilder.CreateTable(
                name: "project_invitations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    redirect_url = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_invitations", x => x.id);
                    table.CheckConstraint("ck_project_invitation_status", "status IN ('pending', 'accepted', 'revoked')");
                    table.ForeignKey(
                        name: "FK_project_invitations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_waitlist",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_waitlist", x => x.id);
                    table.CheckConstraint("ck_project_waitlist_status", "status IN ('pending', 'approved', 'rejected')");
                    table.ForeignKey(
                        name: "FK_project_waitlist_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_invitations_project_id_email_status",
                table: "project_invitations",
                columns: new[] { "project_id", "email", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_project_invitations_project_id_id",
                table: "project_invitations",
                columns: new[] { "project_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_invitations_token_hash",
                table: "project_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_waitlist_project_id_email",
                table: "project_waitlist",
                columns: new[] { "project_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_waitlist_project_id_id",
                table: "project_waitlist",
                columns: new[] { "project_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_invitations");

            migrationBuilder.DropTable(
                name: "project_waitlist");

            migrationBuilder.DropColumn(
                name: "restrictions",
                table: "project_auth_settings");

            migrationBuilder.AddColumn<bool>(
                name: "sign_up",
                table: "project_auth_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE project_auth_settings SET sign_up = CASE WHEN sign_up_mode = 'public' THEN 1 ELSE 0 END");

            migrationBuilder.DropColumn(
                name: "sign_up_mode",
                table: "project_auth_settings");

        }
    }
}
