using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class UserCustomClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_claims",
                table: "users",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "custom_claims",
                table: "users");
        }
    }
}
