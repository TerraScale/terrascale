using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.ProjectOrganizations;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<ProjectOrganizationRecord> ProjectOrganizations => this.Set<ProjectOrganizationRecord>();
    public DbSet<ProjectOrganizationMembership> ProjectOrganizationMemberships => this.Set<ProjectOrganizationMembership>();
    public DbSet<ProjectOrganizationInvitation> ProjectOrganizationInvitations => this.Set<ProjectOrganizationInvitation>();
    public DbSet<ProjectOrganizationDomain> ProjectOrganizationDomains => this.Set<ProjectOrganizationDomain>();

    private static void ConfigureProjectOrganizations(ModelBuilder model)
    {
        model.Entity<ProjectOrganizationRecord>(org =>
        {
            org.ToTable("project_organizations", table =>
            {
                table.HasCheckConstraint("ck_project_organizations_counts", "admins >= 1 AND members >= admins AND (max_members IS NULL OR members <= max_members)");
                table.HasCheckConstraint("ck_project_organizations_slug", "length(slug) BETWEEN 3 AND 63 AND slug = lower(slug)");
                table.HasCheckConstraint("ck_project_organizations_join", "domain_join IN ('off', 'suggest', 'auto')");
            });
            org.HasKey(row => new { row.ProjectId, row.Id });
            org.HasIndex(row => new { row.ProjectId, row.Slug }).IsUnique();
            org.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            org.Property(row => row.PublicMetadata).HasJson(ProjectOrganizationsJson.Default.JsonElement);
            org.Property(row => row.PrivateMetadata).HasJson(ProjectOrganizationsJson.Default.JsonElement);
            Versioned(org);
        });
        model.Entity<ProjectOrganizationMembership>(member =>
        {
            member.ToTable("project_organization_memberships", table => table.HasCheckConstraint("ck_project_organization_memberships_role", "role IN ('admin', 'member')"));
            member.HasKey(row => new { row.ProjectId, row.OrganizationId, row.UserId });
            member.HasOne<ProjectOrganizationRecord>().WithMany().HasForeignKey(row => new { row.ProjectId, row.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
            member.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
            member.HasIndex(row => new { row.ProjectId, row.UserId });
            Versioned(member);
        });
        model.Entity<ProjectOrganizationInvitation>(invite =>
        {
            invite.ToTable("project_organization_invitations");
            invite.HasKey(row => new { row.ProjectId, row.OrganizationId, row.Id });
            invite.HasOne<ProjectOrganizationRecord>().WithMany().HasForeignKey(row => new { row.ProjectId, row.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
            invite.HasIndex(row => row.TokenHash).IsUnique();
            Versioned(invite);
        });
        model.Entity<ProjectOrganizationDomain>(domain =>
        {
            domain.ToTable("project_organization_domains");
            domain.HasKey(row => new { row.ProjectId, row.Domain });
            domain.HasOne<ProjectOrganizationRecord>().WithMany().HasForeignKey(row => new { row.ProjectId, row.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
            Versioned(domain);
        });
    }
}
