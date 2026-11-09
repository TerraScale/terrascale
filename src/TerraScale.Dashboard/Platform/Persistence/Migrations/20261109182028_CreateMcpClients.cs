using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateMcpClients : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_mcp_clients_first_sign_in_completed_at_created_at",
                table: "mcp_clients",
                columns: new[] { "first_sign_in_completed_at", "created_at" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_clients");
        }
    }
}
