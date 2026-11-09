using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Dashboard.Platform.Persistence.Migrations
{
    public partial class CreateInvitationReservations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitation_reservations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    reserved_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    invitation_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: true),
                    known = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitation_reservations", x => x.id);
                    table.CheckConstraint("ck_invitation_reservations_email", "length(email) > 0");
                    table.CheckConstraint("ck_invitation_reservations_id", "length(id) = 26");
                    table.CheckConstraint("ck_invitation_reservations_known", "json_type(known) = 'array'");
                    table.ForeignKey(
                        name: "FK_invitation_reservations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invitation_reservations_organization_id_invitation_id",
                table: "invitation_reservations",
                columns: new[] { "organization_id", "invitation_id" },
                unique: true,
                filter: "invitation_id <> ''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitation_reservations");
        }
    }
}
