using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class AddPaymentGrace : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "payment_grace_until",
                table: "organizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE organizations SET payment_grace_until = strftime('%Y-%m-%d %H:%M:%f', 'now', 'start of day', '+8 days', '-0.001 seconds'), billing_status = 'active' WHERE erasure_id IS NULL AND internal_billing IS NULL AND organizations.status = 'active' AND archived = 0 AND deletion_requested = 0 AND id IN (SELECT organization_id FROM billing WHERE kind = 'stripe' AND subscription_id <> '' AND (status IN ('past_due', 'unpaid') OR status IN ('active', 'trialing') AND organizations.billing_status = 'billing_required'))");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payment_grace_until",
                table: "organizations");
        }
    }
}
