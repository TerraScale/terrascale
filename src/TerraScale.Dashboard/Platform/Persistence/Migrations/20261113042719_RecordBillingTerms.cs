using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordBillingTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "effective_plan_since",
                table: "billing",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expiry_last_logged_at",
                table: "billing",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expiry_unavailable_since",
                table: "billing",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "observed_currency",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "observed_operation_rate",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "observed_operations_allowance",
                table: "billing",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "observed_plan_name",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "observed_pricing_version",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "observed_storage_allowance",
                table: "billing",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "observed_storage_rate",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "overage_starts_at",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "tax_location_status",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
            migrationBuilder.Sql("UPDATE billing SET overage_starts_at = strftime('%Y-%m-%d %H:%M:%f', 'now', 'start of month') WHERE kind = 'stripe'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "effective_plan_since",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "expiry_last_logged_at",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "expiry_unavailable_since",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_currency",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_operation_rate",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_operations_allowance",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_plan_name",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_pricing_version",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_storage_allowance",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "observed_storage_rate",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "overage_starts_at",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "tax_location_status",
                table: "billing");
        }
    }
}
