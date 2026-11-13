using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class RecordBillingSubscriptions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_subscriptions",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ended_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    checked_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_subscriptions", x => new { x.organization_id, x.subscription_id });
                    table.CheckConstraint("ck_billing_subscriptions", "length(subscription_id) > 0 AND length(customer_id) > 0");
                    table.ForeignKey(
                        name: "FK_billing_subscriptions_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_subscriptions");
        }
    }
}
