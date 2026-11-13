using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordSkippedBillingMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_skipped_months",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    month = table.Column<DateTime>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_skipped_months", x => new { x.organization_id, x.subscription_id, x.month });
                    table.ForeignKey(
                        name: "FK_billing_skipped_months_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_skipped_months");
        }
    }
}
