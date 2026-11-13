using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateOverageCharges : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_overage_charges",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    month = table.Column<DateTime>(type: "TEXT", nullable: false),
                    meter = table.Column<string>(type: "TEXT", nullable: false),
                    frozen_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    usage = table.Column<long>(type: "INTEGER", nullable: false),
                    allowance = table.Column<long>(type: "INTEGER", nullable: false),
                    unit_rate = table.Column<decimal>(type: "TEXT", nullable: false),
                    currency = table.Column<string>(type: "TEXT", nullable: false),
                    pricing_version = table.Column<string>(type: "TEXT", nullable: false),
                    plan_name = table.Column<string>(type: "TEXT", nullable: false),
                    quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    amount_cents = table.Column<long>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    skip_reason = table.Column<string>(type: "TEXT", nullable: true),
                    provider_id = table.Column<string>(type: "TEXT", nullable: true),
                    provider_amount_cents = table.Column<long>(type: "INTEGER", nullable: true),
                    accepted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    claim_owner = table.Column<string>(type: "TEXT", nullable: true),
                    claim_until = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_overage_charges", x => x.id);
                    table.CheckConstraint("ck_overage_charge", "usage >= 0 AND allowance >= 0 AND quantity >= 0 AND amount_cents >= 0 AND unit_rate >= 0 AND state IN ('pending', 'charged', 'adopted', 'mismatch', 'skipped', 'nothing_due') AND (provider_id IS NULL) = (accepted_at IS NULL) AND (provider_id IS NULL) = (provider_amount_cents IS NULL) AND (provider_id IS NOT NULL) = (state IN ('charged', 'adopted', 'mismatch')) AND (skip_reason IS NOT NULL) = (state = 'skipped') AND (state = 'nothing_due') = (amount_cents = 0) AND (claim_owner IS NULL) = (claim_until IS NULL)");
                    table.ForeignKey(
                        name: "FK_billing_overage_charges_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_overage_charges_organization_id_month_meter",
                table: "billing_overage_charges",
                columns: new[] { "organization_id", "month", "meter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_overage_charges_provider_id",
                table: "billing_overage_charges",
                column: "provider_id",
                unique: true,
                filter: "provider_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_billing_overage_charges_state_id",
                table: "billing_overage_charges",
                columns: new[] { "state", "id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_overage_charges");
        }
    }
}
