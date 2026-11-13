using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class RecordBillingTermObservations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_term_observations",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    observed_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    pricing_version = table.Column<string>(type: "TEXT", nullable: false),
                    month = table.Column<DateTime>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_billing_term_observations", x => new { x.organization_id, x.subscription_id, x.observed_at, x.pricing_version });
                    table.CheckConstraint("ck_billing_term_observations", "length(subscription_id) > 0");
                    table.ForeignKey(
                        name: "FK_billing_term_observations_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_term_observations_month",
                table: "billing_term_observations",
                column: "month");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_term_observations");

        }
    }
}
