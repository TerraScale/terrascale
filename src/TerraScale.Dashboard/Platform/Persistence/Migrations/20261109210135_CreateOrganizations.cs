using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateOrganizations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    plan_id = table.Column<string>(type: "TEXT", nullable: false),
                    billing_status = table.Column<string>(type: "TEXT", nullable: false),
                    payment_method_validated = table.Column<bool>(type: "INTEGER", nullable: false),
                    spend_cap_cents = table.Column<long>(type: "INTEGER", nullable: false),
                    paid_excess = table.Column<bool>(type: "INTEGER", nullable: false),
                    archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    deletion_requested = table.Column<bool>(type: "INTEGER", nullable: false),
                    internal_billing = table.Column<string>(type: "TEXT", nullable: true),
                    internal_billing_alerts = table.Column<string>(type: "TEXT", nullable: false),
                    audit_retention_days = table.Column<int>(type: "INTEGER", nullable: true),
                    pending_project_deletions = table.Column<string>(type: "TEXT", nullable: false),
                    deleted_project_ids = table.Column<string>(type: "TEXT", nullable: false),
                    erasure_id = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_audit_retention", "audit_retention_days IS NULL OR audit_retention_days BETWEEN 1 AND 90");
                    table.CheckConstraint("ck_organizations_erasure", "erasure_id IS NULL OR (length(erasure_id) = 26 AND internal_billing IS NULL AND internal_billing_alerts = '{}' AND pending_project_deletions = '{}')");
                    table.CheckConstraint("ck_organizations_id", "length(id) = 26");
                    table.CheckConstraint("ck_organizations_kind", "kind IN ('personal', 'business')");
                    table.CheckConstraint("ck_organizations_project_markers", "json_type(pending_project_deletions) = 'object' AND json_type(deleted_project_ids) = 'array' AND json_type(internal_billing_alerts) = 'object'");
                    table.CheckConstraint("ck_organizations_version", "length(version) = 26");
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
