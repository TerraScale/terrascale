using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateStripeCheckouts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stripe_checkouts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    checkout_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    customer_id = table.Column<string>(type: "TEXT", nullable: false),
                    request_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    return_path = table.Column<string>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", nullable: false),
                    interval = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_checkouts", x => x.id);
                    table.CheckConstraint("ck_stripe_checkouts_checkout", "checkout_id LIKE 'cs\\_%' ESCAPE '\\' AND length(request_id) > 0");
                    table.CheckConstraint("ck_stripe_checkouts_id", "length(id) = 26 AND length(organization_id) = 26");
                    table.CheckConstraint("ck_stripe_checkouts_project", "length(project_id) IN (0, 26)");
                    table.ForeignKey(
                        name: "FK_stripe_checkouts_billing_organization_id",
                        column: x => x.organization_id,
                        principalTable: "billing",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stripe_checkouts_stripe_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "stripe_customers",
                        principalColumn: "customer_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stripe_checkouts_checkout_id",
                table: "stripe_checkouts",
                column: "checkout_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stripe_checkouts_customer_id",
                table: "stripe_checkouts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_stripe_checkouts_organization_id",
                table: "stripe_checkouts",
                column: "organization_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stripe_checkouts");
        }
    }
}
