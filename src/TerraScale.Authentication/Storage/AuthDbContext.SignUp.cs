using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<ProjectInvitationRecord> ProjectInvitations => this.Set<ProjectInvitationRecord>();

    public DbSet<WaitlistRecord> Waitlist => this.Set<WaitlistRecord>();

    private static void ConfigureSignUp(ModelBuilder model)
    {
        model.Entity<ProjectInvitationRecord>(invitation =>
        {
            invitation.ToTable("project_invitations", table =>
                table.HasCheckConstraint("ck_project_invitation_status", "status IN ('pending', 'accepted', 'revoked')"));
            invitation.HasKey(row => row.Id);
            invitation.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            invitation.HasIndex(row => row.TokenHash).IsUnique();
            invitation.HasIndex(row => new { row.ProjectId, row.Email, row.Status });
            invitation.HasIndex(row => new { row.ProjectId, row.Id });
            Versioned(invitation);
        });
        model.Entity<WaitlistRecord>(entry =>
        {
            entry.ToTable("project_waitlist", table =>
                table.HasCheckConstraint("ck_project_waitlist_status", "status IN ('pending', 'approved', 'rejected')"));
            entry.HasKey(row => row.Id);
            entry.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entry.HasIndex(row => new { row.ProjectId, row.Email }).IsUnique();
            entry.HasIndex(row => new { row.ProjectId, row.Id });
            Versioned(entry);
        });
    }
}
