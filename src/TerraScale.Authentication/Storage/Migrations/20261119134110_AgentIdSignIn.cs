using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentid_transactions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    client_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    agent_id = table.Column<string>(type: "TEXT", maxLength: 26, nullable: true),
                    owner_user_id = table.Column<string>(type: "TEXT", maxLength: 26, nullable: true),
                    kid = table.Column<string>(type: "TEXT", maxLength: 26, nullable: true),
                    proved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agentid_transactions", x => x.id);
                    table.CheckConstraint("ck_agentid_transactions_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_agentid_transactions_proof", "(proved_at IS NULL) = (agent_id IS NULL) AND (agent_id IS NULL) = (owner_user_id IS NULL) AND (agent_id IS NULL) = (kid IS NULL) AND (completed_at IS NULL OR proved_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_agentid_transactions_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agentid_transactions_expires_at",
                table: "agentid_transactions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_agentid_transactions_owner_user_id",
                table: "agentid_transactions",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agentid_transactions");
        }
    }
}
