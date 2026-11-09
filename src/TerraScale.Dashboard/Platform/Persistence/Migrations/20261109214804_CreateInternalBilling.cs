using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateInternalBilling : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "internal_billing",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    plan_id = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_internal_billing", x => x.id);
                    table.CheckConstraint("ck_internal_billing_id", "length(id) = 26 AND length(organization_id) = 26 AND length(plan_id) = 26");
                    table.CheckConstraint("ck_internal_billing_scope", "scope = 'organization'");
                    table.ForeignKey(
                        name: "FK_internal_billing_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_internal_billing_organization_id",
                table: "internal_billing",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_internal_billing_scope",
                table: "internal_billing",
                column: "scope",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "internal_billing");
        }
    }
}
