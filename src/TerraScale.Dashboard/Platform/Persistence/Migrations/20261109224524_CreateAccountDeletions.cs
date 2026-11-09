using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateAccountDeletions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_deletions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    key_hash = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    nonce = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ciphertext = table.Column<byte[]>(type: "BLOB", nullable: true),
                    tag = table.Column<byte[]>(type: "BLOB", nullable: true),
                    completed_until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_deletions", x => x.id);
                    table.CheckConstraint("ck_account_deletions_id", "length(id) = 26 AND length(version) = 26");
                    table.CheckConstraint("ck_account_deletions_key_hash", "length(key_hash) = 64 AND key_hash NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("ck_account_deletions_state", "(completed_until IS NULL AND length(user_id) > 0 AND length(nonce) = 12 AND length(ciphertext) > 0 AND length(tag) = 16) OR (completed_until IS NOT NULL AND user_id IS NULL AND nonce IS NULL AND ciphertext IS NULL AND tag IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_deletions_completed_until",
                table: "account_deletions",
                column: "completed_until");

            migrationBuilder.CreateIndex(
                name: "IX_account_deletions_key_hash",
                table: "account_deletions",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_deletions_user_id",
                table: "account_deletions",
                column: "user_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_deletions");
        }
    }
}
