using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "share_owner_email",
                table: "agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "agentid_clients",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    owner_user_id = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    redirect_uris = table.Column<string>(type: "TEXT", nullable: false),
                    secret_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    previous_secret_digest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agentid_clients", x => x.id);
                    table.CheckConstraint("ck_agentid_clients_previous_secret", "(previous_secret_digest IS NULL) = (previous_secret_expires_at IS NULL)");
                    table.ForeignKey(
                        name: "FK_agentid_clients_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agentid_clients_owner_user_id",
                table: "agentid_clients",
                column: "owner_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agentid_clients");

            migrationBuilder.DropColumn(
                name: "share_owner_email",
                table: "agents");
        }
    }
}
