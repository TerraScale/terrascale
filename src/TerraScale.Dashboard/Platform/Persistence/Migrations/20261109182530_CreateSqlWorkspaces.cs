using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateSqlWorkspaces : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sql_workspaces",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    database_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    draft = table.Column<string>(type: "TEXT", nullable: false),
                    history = table.Column<string>(type: "TEXT", nullable: false),
                    history_epoch = table.Column<string>(type: "TEXT", nullable: false),
                    receipt = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sql_workspaces", x => x.id);
                    table.CheckConstraint("ck_sql_workspaces_draft", "length(CAST(draft AS BLOB)) <= 16384");
                    table.CheckConstraint("ck_sql_workspaces_history", "json_array_length(history) <= 20");
                    table.CheckConstraint("ck_sql_workspaces_id", "length(id) = 26");
                    table.CheckConstraint("ck_sql_workspaces_scope", "length(organization_id) = 26 AND length(project_id) = 26 AND length(database_id) = 26");
                    table.CheckConstraint("ck_sql_workspaces_version", "length(version) = 26");
                });

            migrationBuilder.CreateIndex(
                name: "IX_sql_workspaces_organization_id_project_id_database_id_user_id",
                table: "sql_workspaces",
                columns: new[] { "organization_id", "project_id", "database_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sql_workspaces_user_id",
                table: "sql_workspaces",
                column: "user_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sql_workspaces");
        }
    }
}
