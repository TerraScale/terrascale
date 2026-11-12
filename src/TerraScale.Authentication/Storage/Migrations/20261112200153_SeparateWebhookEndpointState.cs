using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SeparateWebhookEndpointState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_endpoint_states",
                columns: table => new
                {
                    endpoint_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    first_failure_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    disabled_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_served_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_endpoint_states", x => x.endpoint_id);
                    table.ForeignKey(
                        name: "FK_webhook_endpoint_states_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO webhook_endpoint_states
                    (endpoint_id, project_id, first_failure_at, disabled_at, lease_until, last_served_at, version)
                SELECT id, project_id, first_failure_at, disabled_at, lease_until, last_served_at, version
                FROM webhook_endpoints;
                """);

            migrationBuilder.DropIndex(
                name: "IX_webhook_endpoints_disabled_at_lease_until",
                table: "webhook_endpoints");

            migrationBuilder.DropIndex(
                name: "IX_webhook_endpoints_project_id_last_served_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "disabled_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "first_failure_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "last_served_at",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "webhook_endpoints");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoint_states_disabled_at_lease_until",
                table: "webhook_endpoint_states",
                columns: new[] { "disabled_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoint_states_project_id_last_served_at",
                table: "webhook_endpoint_states",
                columns: new[] { "project_id", "last_served_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                name: "last_served_at",
                table: "webhook_endpoints",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_until",
                table: "webhook_endpoints",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.Sql("""
                UPDATE webhook_endpoints SET
                    first_failure_at = state.first_failure_at,
                    disabled_at = state.disabled_at,
                    lease_until = state.lease_until,
                    last_served_at = state.last_served_at
                FROM webhook_endpoint_states AS state
                WHERE webhook_endpoints.id = state.endpoint_id;
                """);

            migrationBuilder.DropTable(
                name: "webhook_endpoint_states");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoints_disabled_at_lease_until",
                table: "webhook_endpoints",
                columns: new[] { "disabled_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoints_project_id_last_served_at",
                table: "webhook_endpoints",
                columns: new[] { "project_id", "last_served_at" },
                descending: new[] { false, true });
        }
    }
}
