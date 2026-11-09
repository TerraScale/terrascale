using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAllowanceAlerts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "allowance_alerts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    billing_month = table.Column<string>(type: "TEXT", nullable: false),
                    metric = table.Column<string>(type: "TEXT", nullable: false),
                    threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    used = table.Column<long>(type: "INTEGER", nullable: false),
                    allowance = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    email_status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allowance_alerts", x => x.id);
                    table.CheckConstraint("ck_allowance_alerts_amounts", "used >= 0 AND allowance > 0");
                    table.CheckConstraint("ck_allowance_alerts_billing_month", "length(billing_month) = 7 AND substr(billing_month, 5, 1) = '-'");
                    table.CheckConstraint("ck_allowance_alerts_email_status", "email_status IN ('pending', 'accepted', 'unknown', 'rejected', 'no_recipients')");
                    table.CheckConstraint("ck_allowance_alerts_id", "length(id) = 26 AND length(organization_id) = 26");
                    table.CheckConstraint("ck_allowance_alerts_metric", "metric IN ('operations', 'storage', 'spend')");
                    table.CheckConstraint("ck_allowance_alerts_threshold", "threshold IN (80, 100) AND (metric <> 'spend' OR threshold = 100)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_allowance_alerts_organization_id_billing_month_metric_threshold",
                table: "allowance_alerts",
                columns: new[] { "organization_id", "billing_month", "metric", "threshold" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allowance_alerts");
        }
    }
}
