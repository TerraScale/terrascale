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
                name: "mcp_clients",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    redirect_uris = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    first_sign_in_completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    closed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_clients", x => x.id);
                    table.CheckConstraint("ck_mcp_clients_activity", "last_activity_at >= created_at");
                    table.CheckConstraint("ck_mcp_clients_closed", "closed_at IS NULL OR closed_at >= last_activity_at");
                    table.CheckConstraint("ck_mcp_clients_first_sign_in", "(first_sign_in_completed_at IS NULL AND last_activity_at = created_at) OR (first_sign_in_completed_at BETWEEN created_at AND last_activity_at)");
                    table.CheckConstraint("ck_mcp_clients_id", "length(id) = 26 AND id = upper(id)");
                    table.CheckConstraint("ck_mcp_clients_name", "length(name) BETWEEN 1 AND 256");
                    table.CheckConstraint("ck_mcp_clients_redirect_uris", "json_array_length(redirect_uris) BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_mcp_clients_version", "length(version) = 26");
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

            migrationBuilder.CreateTable(
                name: "access_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    last_four = table.Column<string>(type: "TEXT", nullable: false),
                    secret_hash = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<string>(type: "TEXT", nullable: false),
                    mcp_client_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_tokens", x => x.id);
                    table.CheckConstraint("ck_access_tokens_id", "length(id) = 26");
                    table.CheckConstraint("ck_access_tokens_last_four", "length(last_four) = 4");
                    table.CheckConstraint("ck_access_tokens_lifetime", "expires_at > created_at");
                    table.CheckConstraint("ck_access_tokens_name", "length(name) BETWEEN 1 AND 256");
                    table.CheckConstraint("ck_access_tokens_secret_hash", "length(secret_hash) = 64");
                    table.ForeignKey(
                        name: "FK_access_tokens_mcp_clients_mcp_client_id",
                        column: x => x.mcp_client_id,
                        principalTable: "mcp_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_access_tokens_sessions_credential_id",
                        column: x => x.credential_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_codes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    code_hash = table.Column<string>(type: "TEXT", nullable: false),
                    client_id = table.Column<string>(type: "TEXT", nullable: false),
                    credential_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    redirect_uri = table.Column<string>(type: "TEXT", nullable: false),
                    code_challenge = table.Column<string>(type: "TEXT", nullable: false),
                    resource = table.Column<string>(type: "TEXT", nullable: false),
                    client_name = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    token_expires_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_codes", x => x.id);
                    table.CheckConstraint("ck_mcp_codes_code_challenge", "length(code_challenge) = 43");
                    table.CheckConstraint("ck_mcp_codes_code_hash", "length(code_hash) = 64");
                    table.CheckConstraint("ck_mcp_codes_id", "length(id) = 26");
                    table.CheckConstraint("ck_mcp_codes_token_expiry", "token_expires_at > expires_at");
                    table.ForeignKey(
                        name: "FK_mcp_codes_mcp_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "mcp_clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mcp_codes_sessions_credential_id",
                        column: x => x.credential_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_credential_id",
                table: "access_tokens",
                column: "credential_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_mcp_client_id",
                table: "access_tokens",
                column: "mcp_client_id");

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_secret_hash",
                table: "access_tokens",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_user_id_expires_at",
                table: "access_tokens",
                columns: new[] { "user_id", "expires_at" });

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
                name: "IX_mcp_clients_first_sign_in_completed_at_created_at",
                table: "mcp_clients",
                columns: new[] { "first_sign_in_completed_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_mcp_codes_client_id",
                table: "mcp_codes",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_codes_code_hash",
                table: "mcp_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mcp_codes_credential_id",
                table: "mcp_codes",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_mcp_codes_expires_at",
                table: "mcp_codes",
                column: "expires_at");

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
                name: "access_tokens");

            migrationBuilder.DropTable(
                name: "auth_flows");

            migrationBuilder.DropTable(
                name: "mcp_codes");

            migrationBuilder.DropTable(
                name: "mcp_clients");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
