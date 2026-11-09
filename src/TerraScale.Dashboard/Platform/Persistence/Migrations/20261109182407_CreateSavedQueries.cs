using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateSavedQueries : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saved_queries",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    database_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    sql = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_queries", x => x.id);
                    table.CheckConstraint("ck_saved_queries_id", "length(id) = 26");
                    table.CheckConstraint("ck_saved_queries_name", "length(CAST(name AS BLOB)) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_saved_queries_scope", "length(organization_id) = 26 AND length(project_id) = 26 AND length(database_id) IN (0, 26)");
                    table.CheckConstraint("ck_saved_queries_sql", "length(sql) > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_saved_queries_organization_id_project_id_database_id_name",
                table: "saved_queries",
                columns: new[] { "organization_id", "project_id", "database_id", "name" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saved_queries");
        }
    }
}
