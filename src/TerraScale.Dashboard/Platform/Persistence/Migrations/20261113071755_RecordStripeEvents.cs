using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class RecordStripeEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stripe_event_receipts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    received_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    invoice_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    event_created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    processing_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_event_receipts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stripe_event_receipts_received_at",
                table: "stripe_event_receipts",
                column: "received_at");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stripe_event_receipts");
        }
    }
}
