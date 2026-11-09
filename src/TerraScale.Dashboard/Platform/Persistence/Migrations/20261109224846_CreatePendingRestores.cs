using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreatePendingRestores : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_restores",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_database_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_restores", x => x.id);
                    table.CheckConstraint("ck_pending_restores_id", "length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26 AND length(source_database_id) = 26");
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_restores_organization_id_project_id",
                table: "pending_restores",
                columns: new[] { "organization_id", "project_id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_restores");
        }
    }
}
