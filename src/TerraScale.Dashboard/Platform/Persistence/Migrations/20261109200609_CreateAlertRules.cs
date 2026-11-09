using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAlertRules : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    slot = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    metric = table.Column<string>(type: "TEXT", nullable: false),
                    threshold = table.Column<double>(type: "REAL", nullable: false),
                    database_id = table.Column<string>(type: "TEXT", nullable: true),
                    email = table.Column<bool>(type: "INTEGER", nullable: false),
                    webhook_url = table.Column<string>(type: "TEXT", nullable: true),
                    webhook_nonce = table.Column<string>(type: "TEXT", nullable: true),
                    webhook_ciphertext = table.Column<string>(type: "TEXT", nullable: true),
                    webhook_tag = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    since = table.Column<DateTime>(type: "TEXT", nullable: true),
                    alert_id = table.Column<string>(type: "TEXT", nullable: true),
                    value = table.Column<double>(type: "REAL", nullable: true),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.id);
                    table.CheckConstraint("ck_alert_rules_database", "database_id IS NULL OR length(database_id) = 26");
                    table.CheckConstraint("ck_alert_rules_id", "length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26");
                    table.CheckConstraint("ck_alert_rules_metric", "metric IN ('p95_latency_ms', 'error_rate_percent', 'storage_gb', 'operation_units_percent')");
                    table.CheckConstraint("ck_alert_rules_name", "length(name) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_alert_rules_slot", "slot >= 0 AND slot < 20");
                    table.CheckConstraint("ck_alert_rules_state", "(state = 'ok' AND (since IS NULL) = (alert_id IS NULL)) OR (state = 'firing' AND since IS NOT NULL AND length(alert_id) = 26)");
                    table.CheckConstraint("ck_alert_rules_threshold", "threshold > 0 AND threshold <= 1e12");
                    table.CheckConstraint("ck_alert_rules_version", "length(version) = 26");
                    table.CheckConstraint("ck_alert_rules_webhook", "(webhook_url IS NULL AND webhook_nonce IS NULL AND webhook_ciphertext IS NULL AND webhook_tag IS NULL) OR (webhook_url LIKE 'https://%' AND webhook_nonce IS NOT NULL AND webhook_ciphertext IS NOT NULL AND webhook_tag IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_organization_id",
                table: "alert_rules",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_project_id_slot",
                table: "alert_rules",
                columns: new[] { "project_id", "slot" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_rules");
        }
    }
}
