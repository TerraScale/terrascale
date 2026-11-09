using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateDeletionCleanupTargets : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deletion_cleanup_targets",
                columns: table => new
                {
                    cleanup_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_cleanup_targets", x => new { x.cleanup_id, x.kind, x.target_id });
                    table.CheckConstraint("ck_deletion_cleanup_targets_kind", "(kind = 'project' AND length(target_id) = 26) OR (kind = 'customer' AND length(target_id) BETWEEN 1 AND 255)");
                    table.ForeignKey(
                        name: "FK_deletion_cleanup_targets_deletion_cleanup_cleanup_id",
                        column: x => x.cleanup_id,
                        principalTable: "deletion_cleanup",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deletion_cleanup_targets");
        }
    }
}
