using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AgentAssertions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_assertions",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    agent_id = table.Column<string>(type: "TEXT", nullable: false),
                    jti = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    keep_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_assertions", x => new { x.project_id, x.agent_id, x.jti });
                    table.ForeignKey(
                        name: "FK_agent_assertions_agents_project_id_agent_id",
                        columns: x => new { x.project_id, x.agent_id },
                        principalTable: "agents",
                        principalColumns: new[] { "project_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_assertions_keep_until",
                table: "agent_assertions",
                column: "keep_until");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_assertions");
        }
    }
}
