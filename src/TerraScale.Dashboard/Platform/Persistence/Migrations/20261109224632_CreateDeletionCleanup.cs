using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateDeletionCleanup : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deletion_cleanup",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_cleanup", x => x.id);
                    table.CheckConstraint("ck_deletion_cleanup_id", "length(id) = 26 AND length(subject_id) = 26");
                    table.CheckConstraint("ck_deletion_cleanup_scope", "(scope = 'account' AND organization_id IS NULL) OR (scope = 'organization' AND (organization_id IS NULL OR organization_id <> subject_id)) OR (scope = 'project' AND length(organization_id) = 26)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_cleanup_scope_subject_id",
                table: "deletion_cleanup",
                columns: new[] { "scope", "subject_id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deletion_cleanup");
        }
    }
}
