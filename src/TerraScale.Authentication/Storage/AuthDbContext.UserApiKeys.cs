using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.UserApiKeys;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public DbSet<UserApiKeyRecord> UserApiKeys => this.Set<UserApiKeyRecord>();
    public DbSet<UserApiKeyPolicyRecord> UserApiKeyPolicies => this.Set<UserApiKeyPolicyRecord>();

    private static void ConfigureUserApiKeys(ModelBuilder model)
    {
        model.Entity<UserApiKeyRecord>(key =>
        {
            key.ToTable("user_api_keys", table => table.HasCheckConstraint("ck_user_api_keys_expiry", "expires_at IS NULL OR expires_at > created_at"));
            key.HasKey(row => new { row.ProjectId, row.Id });
            key.HasIndex(row => new { row.ProjectId, row.Prefix }).IsUnique();
            key.HasIndex(row => new { row.ProjectId, row.UserId });
            key.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Cascade);
            key.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            key.Property(row => row.SecretHash).HasMaxLength(64).IsRequired();
            key.Property(row => row.Prefix).HasMaxLength(12).IsRequired();
            key.Property(row => row.Scopes).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(key);
        });
        model.Entity<UserApiKeyPolicyRecord>(policy =>
        {
            policy.ToTable("user_api_key_policies");
            policy.HasKey(row => row.ProjectId);
            policy.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            policy.Property(row => row.AllowedScopes).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(policy);
        });
    }
}
