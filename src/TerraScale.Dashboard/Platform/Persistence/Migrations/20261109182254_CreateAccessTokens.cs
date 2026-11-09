using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAccessTokens : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_tokens");
        }
    }
}
