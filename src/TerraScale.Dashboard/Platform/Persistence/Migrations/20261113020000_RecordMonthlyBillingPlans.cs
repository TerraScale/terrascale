using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TerraScale.Dashboard.Platform.Persistence.Migrations;

public sealed partial class RecordMonthlyBillingPlans : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "billing_months",
            columns: table => new
            {
                organization_id = table.Column<string>(type: "TEXT", nullable: false),
                month = table.Column<DateTime>(type: "TEXT", nullable: false),
                subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                plan = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_billing_months", row => new { row.organization_id, row.month });
                table.ForeignKey("FK_billing_months_billing_organization_id", row => row.organization_id, "billing", "organization_id", onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("ck_billing_months_plan", "length(subscription_id) > 0 AND length(plan) > 0");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "billing_months");
    }
}
