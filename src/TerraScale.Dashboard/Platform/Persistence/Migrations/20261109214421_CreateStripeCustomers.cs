using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateStripeCustomers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stripe_customers",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_customers", x => x.id);
                    table.UniqueConstraint("AK_stripe_customers_customer_id", x => x.customer_id);
                    table.CheckConstraint("ck_stripe_customers_customer", "customer_id LIKE 'cus\\_%' ESCAPE '\\'");
                    table.CheckConstraint("ck_stripe_customers_id", "length(id) = 26 AND length(organization_id) = 26");
                    table.ForeignKey(
                        name: "FK_stripe_customers_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stripe_customers_organization_id",
                table: "stripe_customers",
                column: "organization_id",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stripe_customers");
        }
    }
}
