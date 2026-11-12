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
                    draft_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    interval = table.Column<string>(type: "TEXT", nullable: false),
                    checkout_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    refusal_reason = table.Column<string>(type: "TEXT", nullable: false),
                    refund_status = table.Column<string>(type: "TEXT", nullable: false),
                    refund_notice_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    paid_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    customer_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_status = table.Column<string>(type: "TEXT", nullable: false),
                    plan_id = table.Column<string>(type: "TEXT", nullable: false),
                    renewal_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_organization_checkouts", x => x.draft_id);
                    table.CheckConstraint("ck_pending_organization_checkouts_checkout", "length(checkout_id) > 0");
                    table.CheckConstraint("ck_pending_organization_checkouts_creating", "state <> 'creating' OR paid_at IS NOT NULL AND length(customer_id) > 0 AND length(subscription_id) > 0 AND subscription_status IN ('active', 'trialing') AND length(plan_id) > 0 AND renewal_at IS NOT NULL AND refusal_reason = ''");
                    table.CheckConstraint("ck_pending_organization_checkouts_id", "length(user_id) = 26 AND length(draft_id) = 26");
                    table.CheckConstraint("ck_pending_organization_checkouts_interval", "interval IN ('monthly', 'annual')");
                    table.CheckConstraint("ck_pending_organization_checkouts_name", "length(name) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_pending_organization_checkouts_organization", "organization_id = '' OR length(organization_id) = 26");
                    table.CheckConstraint("ck_pending_organization_checkouts_plan", "length(plan) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_pending_organization_checkouts_refusal", "state NOT IN ('canceling', 'refund_pending') OR length(refusal_reason) > 0");
                    table.CheckConstraint("ck_pending_organization_checkouts_state", "state IN ('pending', 'creating', 'canceling', 'refund_pending')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_organization_checkouts_checkout_id",
                table: "pending_organization_checkouts",
                column: "checkout_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_organization_checkouts_user_id",
                table: "pending_organization_checkouts",
                column: "user_id",
                unique: true,
                filter: "state <> 'refund_pending'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_organization_checkouts");
        }
    }
}
