using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WebhookDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_outbox_project_id",
                table: "webhook_outbox");

            migrationBuilder.AddColumn<string>(
                name: "endpoint_id",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "session_id",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "disabled_at",
                table: "webhook_endpoints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "first_failure_at",
                table: "webhook_endpoints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "webhook_endpoints",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    endpoint_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    cycle_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhook_outbox_event_id",
                        column: x => x.event_id,
                        principalTable: "webhook_outbox",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_attempts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    delivery_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    response_code = table.Column<int>(type: "INTEGER", nullable: true),
                    response_body = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_attempts_webhook_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "webhook_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_outbox_occurred_at",
                table: "webhook_outbox",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_outbox_project_id_event_id",
                table: "webhook_outbox",
                columns: new[] { "project_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_attempts_delivery_id_id",
                table: "webhook_attempts",
                columns: new[] { "delivery_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_endpoint_id",
                table: "webhook_deliveries",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_event_id_endpoint_id",
                table: "webhook_deliveries",
                columns: new[] { "event_id", "endpoint_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_status_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_attempts");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_webhook_outbox_occurred_at",
                table: "webhook_outbox");

            migrationBuilder.DropIndex(
                name: "IX_webhook_outbox_project_id_event_id",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "endpoint_id",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "disabled_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "first_failure_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "webhook_endpoints");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_outbox_project_id",
                table: "webhook_outbox",
                column: "project_id");
        }
    }
}
