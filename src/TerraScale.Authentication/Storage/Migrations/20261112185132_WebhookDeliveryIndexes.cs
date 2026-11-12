using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WebhookDeliveryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_deliveries_endpoint_id",
                table: "webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_webhook_deliveries_status_next_attempt_at",
                table: "webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_webhook_attempts_delivery_id_id",
                table: "webhook_attempts");

            migrationBuilder.AddColumn<DateTime>(
                name: "last_served_at",
                table: "webhook_endpoints",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "email_deliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoints_disabled_at_lease_until",
                table: "webhook_endpoints",
                columns: new[] { "disabled_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoints_project_id_last_served_at",
                table: "webhook_endpoints",
                columns: new[] { "project_id", "last_served_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_endpoint_id_status_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "endpoint_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_attempts_delivery_id_id",
                table: "webhook_attempts",
                columns: new[] { "delivery_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_kind_status_lease_until",
                table: "email_deliveries",
                columns: new[] { "kind", "status", "lease_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_endpoints_disabled_at_lease_until",
                table: "webhook_endpoints");

            migrationBuilder.DropIndex(
                name: "IX_webhook_endpoints_project_id_last_served_at",
                table: "webhook_endpoints");

            migrationBuilder.DropIndex(
                name: "IX_webhook_deliveries_endpoint_id_status_next_attempt_at",
                table: "webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_webhook_attempts_delivery_id_id",
                table: "webhook_attempts");

            migrationBuilder.DropIndex(
                name: "IX_email_deliveries_kind_status_lease_until",
                table: "email_deliveries");

            migrationBuilder.DropColumn(
                name: "last_served_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "email_deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_endpoint_id",
                table: "webhook_deliveries",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_status_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_attempts_delivery_id_id",
                table: "webhook_attempts",
                columns: new[] { "delivery_id", "id" });
        }
    }
}
