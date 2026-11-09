using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class Initial : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_flows",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    tag = table.Column<byte[]>(type: "BLOB", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_flows", x => x.id);
                    table.CheckConstraint("ck_auth_flows_id", "length(id) = 26");
                    table.CheckConstraint("ck_auth_flows_lifetime", "expires_at > created_at");
                    table.CheckConstraint("ck_auth_flows_nonce", "length(nonce) = 12");
                    table.CheckConstraint("ck_auth_flows_purpose", "purpose IN ('ts-dashboard-oidc', 'ts-dashboard-step-up')");
                    table.CheckConstraint("ck_auth_flows_tag", "length(tag) = 16");
                    table.CheckConstraint("ck_auth_flows_token_hash", "length(token_hash) = 64");
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    device = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    tag = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.CheckConstraint("ck_sessions_id", "length(id) = 26");
                    table.CheckConstraint("ck_sessions_kind", "(kind = 'browser' AND length(token_hash) = 64) OR (kind = 'credential' AND token_hash IS NULL)");
                    table.CheckConstraint("ck_sessions_nonce", "length(nonce) = 12");
                    table.CheckConstraint("ck_sessions_tag", "length(tag) = 16");
                    table.CheckConstraint("ck_sessions_user_id", "length(user_id) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_sessions_version", "length(version) = 26");
                });

            migrationBuilder.CreateIndex(
                name: "IX_auth_flows_expires_at",
                table: "auth_flows",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_auth_flows_token_hash",
                table: "auth_flows",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_token_hash",
                table: "sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id_kind_expires_at",
                table: "sessions",
                columns: new[] { "user_id", "kind", "expires_at" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_flows");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
