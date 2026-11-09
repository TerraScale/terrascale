using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateOrganizationProjects : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_projects",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    region = table.Column<string>(type: "TEXT", nullable: false),
                    backup_region = table.Column<string>(type: "TEXT", nullable: false),
                    regions = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    is_default = table.Column<bool>(type: "INTEGER", nullable: false),
                    spend_cap_cents = table.Column<long>(type: "INTEGER", nullable: false),
                    read_limit = table.Column<long>(type: "INTEGER", nullable: false),
                    write_limit = table.Column<long>(type: "INTEGER", nullable: false),
                    replication_limit = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_projects", x => x.id);
                    table.CheckConstraint("ck_organization_projects_id", "length(id) = 26");
                    table.CheckConstraint("ck_organization_projects_limits", "read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0");
                    table.CheckConstraint("ck_organization_projects_regions", "json_type(regions) = 'array'");
                    table.ForeignKey(
                        name: "FK_organization_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_projects_organization_id",
                table: "organization_projects",
                column: "organization_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_projects");
        }
    }
}
