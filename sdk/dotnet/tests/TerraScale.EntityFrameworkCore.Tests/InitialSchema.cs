using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TerraScale.EntityFrameworkCore.Tests;

[DbContext(typeof(AppDbContext))]
[Migration(Id)]
public sealed class InitialSchema : Migration
{
    public const string Id = "20261109000000_InitialSchema";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Accounts",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                Version = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Verified = table.Column<bool>(type: "INTEGER", nullable: false),
                Avatar = table.Column<byte[]>(type: "BLOB", nullable: true),
                Preferences = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Accounts", row => row.Id);
                table.CheckConstraint("CK_Accounts_Email", "instr(\"Email\", '@') > 1");
            });
        migrationBuilder.CreateTable(
            name: "Sessions",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                AccountId = table.Column<string>(type: "TEXT", nullable: false),
                Device = table.Column<string>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sessions", row => row.Id);
                table.ForeignKey("FK_Sessions_Accounts_AccountId", row => row.AccountId, "Accounts", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_Accounts_Email", table: "Accounts", column: "Email", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Sessions_AccountId", table: "Sessions", column: "AccountId");
        migrationBuilder.CreateIndex(name: "IX_Sessions_ExpiresAt", table: "Sessions", column: "ExpiresAt");
    }
}
