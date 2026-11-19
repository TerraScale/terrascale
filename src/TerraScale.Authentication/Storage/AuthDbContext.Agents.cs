using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Agents;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<AgentRecord> Agents => this.Set<AgentRecord>();
    public DbSet<AgentKeyRecord> AgentKeys => this.Set<AgentKeyRecord>();
    public DbSet<AgentPolicyRecord> AgentPolicies => this.Set<AgentPolicyRecord>();
    public DbSet<AgentAssertionRecord> AgentAssertions => this.Set<AgentAssertionRecord>();

    private static void ConfigureAgents(ModelBuilder model)
    {
        model.Entity<AgentRecord>(agent =>
        {
            agent.ToTable("agents", table =>
            {
                table.HasCheckConstraint("ck_agents_status", "status IN ('active', 'revoked') AND (status = 'revoked') = (revoked_at IS NOT NULL)");
                table.HasCheckConstraint("ck_agents_expiry", "expires_at IS NULL OR expires_at > created_at");
            });
            agent.HasKey(row => new { row.ProjectId, row.Id });
            agent.HasIndex(row => new { row.ProjectId, row.OwnerUserId });
            agent.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            agent.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            agent.Property(row => row.Name).HasMaxLength(64).IsRequired();
            agent.Property(row => row.Status).HasMaxLength(8).IsRequired();
            agent.Property(row => row.OwnerAuthStamp).HasMaxLength(26).IsRequired();
            agent.Property(row => row.AllowedScopes).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(agent);
        });
        model.Entity<AgentKeyRecord>(key =>
        {
            key.ToTable("agent_keys");
            key.HasKey(row => new { row.ProjectId, row.Kid });
            key.HasIndex(row => new { row.ProjectId, row.Thumbprint }).IsUnique();
            key.HasIndex(row => new { row.ProjectId, row.AgentId });
            key.HasOne<AgentRecord>().WithMany().HasForeignKey(row => new { row.ProjectId, row.AgentId }).OnDelete(DeleteBehavior.Cascade);
            key.Property(row => row.PublicJwk).HasMaxLength(160).IsRequired();
            key.Property(row => row.Thumbprint).HasMaxLength(43).IsRequired();
            Versioned(key);
        });
        model.Entity<AgentPolicyRecord>(policy =>
        {
            policy.ToTable("agent_policies");
            policy.HasKey(row => row.ProjectId);
            policy.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            policy.Property(row => row.AllowedScopes).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(policy);
        });
        model.Entity<AgentAssertionRecord>(assertion =>
        {
            assertion.ToTable("agent_assertions");
            assertion.HasKey(row => new { row.ProjectId, row.AgentId, row.Jti });
            assertion.HasIndex(row => row.KeepUntil);
            assertion.HasOne<AgentRecord>().WithMany().HasForeignKey(row => new { row.ProjectId, row.AgentId }).OnDelete(DeleteBehavior.Cascade);
            assertion.Property(row => row.Jti).HasMaxLength(AgentTokens.MaxJtiLength).IsRequired();
            Versioned(assertion);
        });
    }
}
