using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAlertEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    rule_id = table.Column<string>(type: "TEXT", nullable: false),
                    alert_id = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<double>(type: "REAL", nullable: false),
                    database_name = table.Column<string>(type: "TEXT", nullable: true),
                    at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    email_status = table.Column<string>(type: "TEXT", nullable: false),
                    webhook_status = table.Column<string>(type: "TEXT", nullable: false),
                    webhook_attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_events", x => x.id);
                    table.CheckConstraint("ck_alert_events_email_status", "email_status IN ('pending', 'none', 'accepted', 'unknown', 'rejected', 'no_recipients')");
                    table.CheckConstraint("ck_alert_events_id", "length(id) = 26 AND length(rule_id) = 26 AND length(alert_id) = 26");
                    table.CheckConstraint("ck_alert_events_pending", "email_status = 'pending' OR webhook_status = 'pending'");
                    table.CheckConstraint("ck_alert_events_state", "state IN ('firing', 'resolved')");
                    table.CheckConstraint("ck_alert_events_webhook_attempts", "webhook_attempts BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_alert_events_webhook_status", "webhook_status IN ('pending', 'none', 'delivered', 'failed')");
                    table.ForeignKey(
                        name: "FK_alert_events_alert_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_rule_id_id",
                table: "alert_events",
                columns: new[] { "rule_id", "id" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_events");
        }
    }
}
