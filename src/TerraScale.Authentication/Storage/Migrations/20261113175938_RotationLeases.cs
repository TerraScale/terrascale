using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RotationLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rotation_leases",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rotation_leases", x => x.name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rotation_leases");
        }
    }
}
