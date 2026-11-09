using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateBilling : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false),
                    erasure_id = table.Column<string>(type: "TEXT", nullable: true),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    internal_reservation = table.Column<string>(type: "TEXT", nullable: true),
                    customer_id = table.Column<string>(type: "TEXT", nullable: false),
                    plan = table.Column<string>(type: "TEXT", nullable: false),
                    interval = table.Column<string>(type: "TEXT", nullable: false),
                    return_path = table.Column<string>(type: "TEXT", nullable: false),
                    request_id = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    checkout_id = table.Column<string>(type: "TEXT", nullable: false),
                    checkout_project_id = table.Column<string>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<string>(type: "TEXT", nullable: false),
                    effective_plan = table.Column<string>(type: "TEXT", nullable: false),
                    effective_interval = table.Column<string>(type: "TEXT", nullable: false),
                    renewal_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    trial_used = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing", x => x.organization_id);
                    table.CheckConstraint("ck_billing_customer", "customer_id = '' OR customer_id LIKE 'cus\\_%' ESCAPE '\\'");
                    table.CheckConstraint("ck_billing_erasure", "erasure_id IS NULL AND length(request_id) > 0 OR length(erasure_id) = 26 AND length(request_id) = 0");
                    table.CheckConstraint("ck_billing_id", "length(organization_id) = 26 AND length(version) = 26");
                    table.CheckConstraint("ck_billing_internal", "(kind = 'internal') = (internal_reservation IS NOT NULL) AND (kind = 'stripe' OR customer_id = '' AND checkout_id = '' AND subscription_id = '')");
                    table.CheckConstraint("ck_billing_kind", "kind IN ('stripe', 'internal') AND state IN ('reserved', 'active', 'deleting')");
                    table.CheckConstraint("ck_billing_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "FK_billing_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_customer_id",
                table: "billing",
                column: "customer_id",
                unique: true,
                filter: "customer_id <> ''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing");
        }
    }
}
