using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAuthFlows : Migration
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

            migrationBuilder.CreateIndex(
                name: "IX_auth_flows_expires_at",
                table: "auth_flows",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_auth_flows_token_hash",
                table: "auth_flows",
                column: "token_hash",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_flows");
        }
    }
}
