using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "overage_reported_through",
                table: "billing",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_location_status",
                table: "billing",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "overage_reported_through",
                table: "billing");

            migrationBuilder.DropColumn(
                name: "tax_location_status",
                table: "billing");
        }
    }
}
