using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordMonthlyBillingPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_months",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    month = table.Column<DateTime>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", nullable: false),
                    plan_name = table.Column<string>(type: "TEXT", nullable: false),
                    operations_allowance = table.Column<long>(type: "INTEGER", nullable: false),
                    storage_allowance = table.Column<long>(type: "INTEGER", nullable: false),
                    operation_rate = table.Column<decimal>(type: "TEXT", nullable: false),
                    storage_rate = table.Column<decimal>(type: "TEXT", nullable: false),
                    currency = table.Column<string>(type: "TEXT", nullable: false),
                    pricing_version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_months", x => new { x.organization_id, x.subscription_id, x.month });
                    table.CheckConstraint("ck_billing_months_plan", "length(subscription_id) > 0 AND length(pricing_version) > 0");
                    table.ForeignKey(
                        name: "FK_billing_months_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_months");
        }
    }
}
