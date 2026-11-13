using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "payment_grace_until",
                table: "organizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE organizations SET payment_grace_until = strftime('%Y-%m-%d %H:%M:%f', 'now') WHERE billing_status = 'billing_required'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payment_grace_until",
                table: "organizations");
        }
    }
}
