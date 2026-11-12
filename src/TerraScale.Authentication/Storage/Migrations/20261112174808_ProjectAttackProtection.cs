using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectAttackProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "attack_protection",
                table: "project_auth_settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "account_locks",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    failures = table.Column<int>(type: "INTEGER", nullable: false),
                    locked_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    unlock_hash = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_locks", x => new { x.instance_id, x.user_id });
                    table.CheckConstraint("ck_account_locks_failures", "failures BETWEEN 0 AND 10");
                    table.ForeignKey(
                        name: "FK_account_locks_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "known_devices",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_known_devices", x => new { x.instance_id, x.user_id, x.fingerprint });
                    table.ForeignKey(
                        name: "FK_known_devices_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_locks_user_id",
                table: "account_locks",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_known_devices_last_seen",
                table: "known_devices",
                column: "last_seen");

            migrationBuilder.CreateIndex(
                name: "IX_known_devices_user_id",
                table: "known_devices",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_locks");

            migrationBuilder.DropTable(
                name: "known_devices");

            migrationBuilder.DropColumn(
                name: "attack_protection",
                table: "project_auth_settings");
        }
    }
}
