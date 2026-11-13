using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class RecordBillingTerms : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_terms",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    effective_from = table.Column<DateTime>(type: "TEXT", nullable: false),
                    pricing_version = table.Column<string>(type: "TEXT", nullable: false),
                    basis = table.Column<string>(type: "TEXT", nullable: false),
                    learned_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", nullable: false),
                    plan_name = table.Column<string>(type: "TEXT", nullable: false),
                    operations_allowance = table.Column<long>(type: "INTEGER", nullable: false),
                    storage_allowance = table.Column<long>(type: "INTEGER", nullable: false),
                    operation_rate = table.Column<decimal>(type: "TEXT", nullable: false),
                    storage_rate = table.Column<decimal>(type: "TEXT", nullable: false),
                    currency = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_terms", x => new { x.organization_id, x.subscription_id, x.effective_from, x.pricing_version, x.basis });
                    table.CheckConstraint("ck_billing_terms", "length(subscription_id) > 0 AND basis IN ('start_date', 'event', 'first_check', 'unestablished')");
                    table.ForeignKey(
                        name: "FK_billing_terms_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_terms");
        }
    }
}
