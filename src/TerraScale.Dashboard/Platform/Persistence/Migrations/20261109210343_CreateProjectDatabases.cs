using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateProjectDatabases : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_databases",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    previous_name = table.Column<string>(type: "TEXT", nullable: true),
                    conflict_of = table.Column<string>(type: "TEXT", nullable: true),
                    region = table.Column<string>(type: "TEXT", nullable: false),
                    regions = table.Column<string>(type: "TEXT", nullable: false),
                    all_regions = table.Column<bool>(type: "INTEGER", nullable: false),
                    placement_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    spend_cap_cents = table.Column<long>(type: "INTEGER", nullable: false),
                    read_limit = table.Column<long>(type: "INTEGER", nullable: false),
                    write_limit = table.Column<long>(type: "INTEGER", nullable: false),
                    replication_limit = table.Column<long>(type: "INTEGER", nullable: false),
                    backup_retention_days = table.Column<int>(type: "INTEGER", nullable: false),
                    branched_from = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_databases", x => x.id);
                    table.CheckConstraint("ck_project_databases_id", "length(id) = 26");
                    table.CheckConstraint("ck_project_databases_limits", "read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0 AND backup_retention_days >= 0");
                    table.CheckConstraint("ck_project_databases_name", "length(name) > 0");
                    table.CheckConstraint("ck_project_databases_placement_revision", "placement_revision >= 0");
                    table.CheckConstraint("ck_project_databases_regions", "json_type(regions) = 'array'");
                    table.ForeignKey(
                        name: "FK_project_databases_organization_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "organization_projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_databases_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_databases_organization_id",
                table: "project_databases",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_databases_project_id_name",
                table: "project_databases",
                columns: new[] { "project_id", "name" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_databases");
        }
    }
}
