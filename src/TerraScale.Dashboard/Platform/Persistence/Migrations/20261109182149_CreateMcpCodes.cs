using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateMcpCodes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_codes");
        }
    }
}
