using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateBackgroundJobs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    owner = table.Column<string>(type: "TEXT", nullable: false),
                    checkpoint = table.Column<string>(type: "TEXT", nullable: false),
                    available_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    attempted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    succeeded_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    cursor = table.Column<string>(type: "TEXT", nullable: true),
                    next_cursor = table.Column<string>(type: "TEXT", nullable: true),
                    delivery_id = table.Column<string>(type: "TEXT", nullable: true),
                    payload = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_jobs", x => x.id);
                    table.CheckConstraint("ck_background_jobs_delivery", "(payload IS NULL AND next_cursor IS NULL AND delivery_id IS NULL) OR (payload IS NOT NULL AND (next_cursor IS NULL OR length(next_cursor) = 26) AND length(delivery_id) = 26)");
                    table.CheckConstraint("ck_background_jobs_id", "length(id) BETWEEN 1 AND 256 AND length(owner) = 26 AND length(checkpoint) = 26");
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_jobs");
        }
    }
}
