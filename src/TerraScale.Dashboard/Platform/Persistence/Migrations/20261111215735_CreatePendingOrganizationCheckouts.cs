using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreatePendingOrganizationCheckouts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_organization_checkouts",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    draft_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", nullable: false),
                    interval = table.Column<string>(type: "TEXT", nullable: false),
                    checkout_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_organization_checkouts", x => x.user_id);
                    table.CheckConstraint("ck_pending_organization_checkouts_id", "length(user_id) = 26 AND length(draft_id) = 26");
                    table.CheckConstraint("ck_pending_organization_checkouts_name", "length(name) BETWEEN 1 AND 128");
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_organization_checkouts_checkout_id",
                table: "pending_organization_checkouts",
                column: "checkout_id",
                unique: true,
                filter: "checkout_id <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_pending_organization_checkouts_draft_id",
                table: "pending_organization_checkouts",
                column: "draft_id",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_organization_checkouts");
        }
    }
}
