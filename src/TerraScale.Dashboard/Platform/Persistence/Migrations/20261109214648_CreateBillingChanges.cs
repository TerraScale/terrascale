using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateBillingChanges : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_changes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    request_id = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", nullable: false),
                    interval = table.Column<string>(type: "TEXT", nullable: false),
                    source_plan_key = table.Column<string>(type: "TEXT", nullable: false),
                    source_interval = table.Column<string>(type: "TEXT", nullable: false),
                    source_price_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_price_id = table.Column<string>(type: "TEXT", nullable: false),
                    period_end = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_changes", x => x.id);
                    table.CheckConstraint("ck_billing_changes_action", "action IN ('change', 'undo') AND status IN ('reserved', 'applying', 'applied', 'abandoned')");
                    table.CheckConstraint("ck_billing_changes_id", "length(id) = 26 AND length(version) = 26 AND length(organization_id) = 26");
                    table.CheckConstraint("ck_billing_changes_request", "length(request_id) > 0 AND length(subscription_id) > 0 AND revision >= 1");
                    table.ForeignKey(
                        name: "FK_billing_changes_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_changes_organization_id",
                table: "billing_changes",
                column: "organization_id",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_changes");
        }
    }
}
