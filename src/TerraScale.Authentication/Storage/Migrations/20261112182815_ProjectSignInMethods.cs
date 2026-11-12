using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectSignInMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE users ADD COLUMN username TEXT CONSTRAINT ck_users_username
                CHECK (username IS NULL OR (length(username) BETWEEN 3 AND 32 AND username = lower(username) AND username NOT GLOB '*[^a-z0-9_.]*'))
                """);

            migrationBuilder.AddColumn<bool>(
                name: "magic_link",
                table: "project_auth_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "username",
                table: "project_auth_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "passcodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_instance_id_username",
                table: "users",
                columns: new[] { "instance_id", "username" },
                unique: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_instance_id_username",
                table: "users");

            migrationBuilder.DropColumn(
                name: "username",
                table: "users");

            migrationBuilder.DropColumn(
                name: "magic_link",
                table: "project_auth_settings");

            migrationBuilder.DropColumn(
                name: "username",
                table: "project_auth_settings");

            migrationBuilder.DropColumn(
                name: "username",
                table: "passcodes");
        }
    }
}
