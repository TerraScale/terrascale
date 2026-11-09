using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateLogDrains : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "log_drains",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    revision = table.Column<string>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    header_name = table.Column<string>(type: "TEXT", nullable: true),
                    header_nonce = table.Column<string>(type: "TEXT", nullable: true),
                    header_ciphertext = table.Column<string>(type: "TEXT", nullable: true),
                    header_tag = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_success_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_error_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_error_status = table.Column<int>(type: "INTEGER", nullable: true),
                    last_gap_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_drains", x => x.id);
                    table.CheckConstraint("ck_log_drains_error", "(last_error_at IS NULL) = (last_error_status IS NULL) AND (last_error_status IS NULL OR last_error_status BETWEEN 0 AND 599)");
                    table.CheckConstraint("ck_log_drains_header", "(header_name IS NULL AND header_nonce IS NULL AND header_ciphertext IS NULL AND header_tag IS NULL) OR (length(header_name) BETWEEN 1 AND 64 AND header_nonce IS NOT NULL AND header_ciphertext IS NOT NULL AND header_tag IS NOT NULL)");
                    table.CheckConstraint("ck_log_drains_id", "length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26");
                    table.CheckConstraint("ck_log_drains_revision", "length(revision) = 26");
                    table.CheckConstraint("ck_log_drains_url", "url LIKE 'https://%' AND length(url) <= 2048");
                });

            migrationBuilder.CreateIndex(
                name: "IX_log_drains_organization_id",
                table: "log_drains",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_log_drains_project_id",
                table: "log_drains",
                column: "project_id",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "log_drains");
        }
    }
}
