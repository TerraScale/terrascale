using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAccounts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    subject_hash = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    phone = table.Column<string>(type: "TEXT", nullable: false),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    onboarding_completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    onboarding_step = table.Column<string>(type: "TEXT", nullable: false),
                    organization_ids = table.Column<string>(type: "TEXT", nullable: false),
                    personal_organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    personal_claim_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    active_organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    active_project_id = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    erasure_id = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_erasure", "(erasure_id IS NULL AND length(user_id) BETWEEN 1 AND 256) OR (length(erasure_id) = 26 AND user_id IS NULL AND name = '' AND email = '' AND phone = '' AND organization_ids = '[]' AND personal_organization_id = '' AND active_organization_id = '' AND active_project_id = '')");
                    table.CheckConstraint("ck_accounts_id", "length(id) = 26");
                    table.CheckConstraint("ck_accounts_organization_ids", "json_type(organization_ids) = 'array'");
                    table.CheckConstraint("ck_accounts_personal_organization_id", "length(personal_organization_id) IN (0, 26)");
                    table.CheckConstraint("ck_accounts_subject_hash", "length(subject_hash) = 64");
                    table.CheckConstraint("ck_accounts_version", "length(version) = 26");
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_subject_hash",
                table: "accounts",
                column: "subject_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_accounts_user_id",
                table: "accounts",
                column: "user_id",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts");
        }
    }
}
