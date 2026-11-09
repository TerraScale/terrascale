using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateLogDrainCursors : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "log_drain_cursors",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    drain_id = table.Column<string>(type: "TEXT", nullable: false),
                    database_id = table.Column<string>(type: "TEXT", nullable: false),
                    cursor = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_log_drain_cursors", x => x.id);
                    table.CheckConstraint("ck_log_drain_cursors_cursor", "length(cursor) = 26");
                    table.CheckConstraint("ck_log_drain_cursors_id", "length(id) = 26 AND length(drain_id) = 26 AND length(database_id) = 26");
                    table.ForeignKey(
                        name: "FK_log_drain_cursors_log_drains_drain_id",
                        column: x => x.drain_id,
                        principalTable: "log_drains",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_log_drain_cursors_drain_id_database_id",
                table: "log_drain_cursors",
                columns: new[] { "drain_id", "database_id" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "log_drain_cursors");
        }
    }
}
