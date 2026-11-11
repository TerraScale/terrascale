using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class AddDeletionCleanupSettlement : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "acknowledged_at",
                table: "deletion_cleanup",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "billing_settled_at",
                table: "deletion_cleanup",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acknowledged_at",
                table: "deletion_cleanup");

            migrationBuilder.DropColumn(
                name: "billing_settled_at",
                table: "deletion_cleanup");
        }
    }
}
