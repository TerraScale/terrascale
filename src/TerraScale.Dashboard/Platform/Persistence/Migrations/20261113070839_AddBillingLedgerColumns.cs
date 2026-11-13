using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class AddBillingLedgerColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.Sql("UPDATE billing SET overage_starts_at = strftime('%Y-%m-%d %H:%M:%f', 'now', 'start of month', '+1 month') WHERE kind = 'stripe'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expiry_last_logged_at",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "expiry_unavailable_since",
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
