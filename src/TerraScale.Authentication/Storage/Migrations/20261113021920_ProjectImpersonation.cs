using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProjectImpersonation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_id",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "webhook_outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "impersonations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    actor_id = table.Column<string>(type: "TEXT", nullable: false),
                    actor_email = table.Column<string>(type: "TEXT", nullable: false),
                    actor_stamp = table.Column<string>(type: "TEXT", nullable: false),
                    actor_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    user_stamp = table.Column<string>(type: "TEXT", nullable: false),
                    user_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    client_id = table.Column<string>(type: "TEXT", nullable: false),
                    ticket_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ticket_expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    redeemed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ended_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    cookie_hash = table.Column<string>(type: "TEXT", nullable: true),
                    ticket_owner = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_impersonations", x => x.id);
                    table.CheckConstraint("ck_impersonations_expiry", "expires_at IS NULL OR (redeemed_at IS NOT NULL AND unixepoch(expires_at) BETWEEN unixepoch(redeemed_at) AND unixepoch(redeemed_at) + 3600)");
                    table.CheckConstraint("ck_impersonations_reason", "length(reason) BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_impersonations_tenant", "instance_id <> 'terrascale'");
                    table.CheckConstraint("ck_impersonations_ticket", "length(ticket_hash) = 64");
                    table.ForeignKey(
                        name: "FK_impersonations_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_impersonations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_impersonations_actor_id",
                table: "impersonations",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_impersonations_cookie_hash",
                table: "impersonations",
                column: "cookie_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_impersonations_instance_id_expires_at",
                table: "impersonations",
                columns: new[] { "instance_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_impersonations_ticket_hash",
                table: "impersonations",
                column: "ticket_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_impersonations_user_id",
                table: "impersonations",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "impersonations");

            migrationBuilder.DropColumn(
                name: "actor_id",
                table: "webhook_outbox");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "webhook_outbox");
        }
    }
}
