using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class AllowAlertSecretHandoff : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_flows_purpose",
                table: "auth_flows");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_flows_purpose",
                table: "auth_flows",
                sql: "purpose IN ('ts-dashboard-oidc', 'ts-dashboard-step-up', 'ts-dashboard-alert-secret')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM auth_flows WHERE purpose = 'ts-dashboard-alert-secret'");

            migrationBuilder.DropCheckConstraint(
                name: "ck_auth_flows_purpose",
                table: "auth_flows");

            migrationBuilder.AddCheckConstraint(
                name: "ck_auth_flows_purpose",
                table: "auth_flows",
                sql: "purpose IN ('ts-dashboard-oidc', 'ts-dashboard-step-up')");
        }
    }
}
