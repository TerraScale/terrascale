using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentInboxPurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_passcodes_purpose",
                table: "passcodes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_passcodes_purpose",
                table: "passcodes",
                sql: "purpose IS NULL OR purpose IN ('password-reset', 'sign-up', 'step-up', 'enrollment-inbox')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_passcodes_purpose",
                table: "passcodes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_passcodes_purpose",
                table: "passcodes",
                sql: "purpose IS NULL OR purpose IN ('password-reset', 'sign-up', 'step-up')");
        }
    }
}
