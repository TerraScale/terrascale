using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class AccountDeletionRow
{
    public required string Id { get; init; }

    public required string KeyHash { get; init; }

    public string? UserId { get; set; }

    public byte[]? Nonce { get; set; }

    public byte[]? Ciphertext { get; set; }

    public byte[]? Tag { get; set; }

    public DateTime? CompletedUntil { get; set; }

    public required DateTime CreatedAt { get; init; }

    public required string Version { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<AccountDeletionRow>
    {
        public void Configure(EntityTypeBuilder<AccountDeletionRow> builder)
        {
            builder.ToTable("account_deletions", table =>
            {
                table.HasCheckConstraint("ck_account_deletions_id", "length(id) = 26 AND length(version) = 26");
                table.HasCheckConstraint("ck_account_deletions_key_hash", "length(key_hash) = 64 AND key_hash NOT GLOB '*[^0-9a-f]*'");
                table.HasCheckConstraint("ck_account_deletions_state",
                    "(completed_until IS NULL AND length(user_id) > 0 AND length(nonce) = 12 AND length(ciphertext) > 0 AND length(tag) = 16) OR " +
                    "(completed_until IS NOT NULL AND user_id IS NULL AND nonce IS NULL AND ciphertext IS NULL AND tag IS NULL)");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.KeyHash).IsUnique();
            builder.HasIndex(row => row.UserId);
            builder.HasIndex(row => row.CompletedUntil);
            builder.Property(row => row.Version).IsConcurrencyToken();
        }
    }
}

public sealed class DeletionCleanupRow
{
    public const string Account = "account";
    public const string Organization = "organization";
    public const string Project = "project";

    public required string Id { get; init; }

    public required string Scope { get; init; }

    public required string SubjectId { get; init; }

    public string? OrganizationId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public DateTime? AcknowledgedAt { get; init; }

    public DateTime? BillingSettledAt { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<DeletionCleanupRow>
    {
        public void Configure(EntityTypeBuilder<DeletionCleanupRow> builder)
        {
            builder.ToTable("deletion_cleanup", table =>
            {
                table.HasCheckConstraint("ck_deletion_cleanup_id", "length(id) = 26 AND length(subject_id) = 26");
                table.HasCheckConstraint("ck_deletion_cleanup_scope",
                    $"(scope = '{Account}' AND organization_id IS NULL) OR " +
                    $"(scope = '{Organization}' AND (organization_id IS NULL OR organization_id <> subject_id)) OR " +
                    $"(scope = '{Project}' AND length(organization_id) = 26)");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => new { row.Scope, row.SubjectId });
        }
    }
}

public sealed class DeletionCleanupTargetRow
{
    public const string Project = "project";
    public const string Customer = "customer";

    public required string CleanupId { get; init; }

    public required string Kind { get; init; }

    public required string TargetId { get; init; }

    public sealed class Configuration : IEntityTypeConfiguration<DeletionCleanupTargetRow>
    {
        public void Configure(EntityTypeBuilder<DeletionCleanupTargetRow> builder)
        {
            builder.ToTable("deletion_cleanup_targets", table =>
            {
                table.HasCheckConstraint("ck_deletion_cleanup_targets_kind",
                    $"(kind = '{Project}' AND length(target_id) = 26) OR (kind = '{Customer}' AND length(target_id) BETWEEN 1 AND 255)");
            });
            builder.HasKey(row => new { row.CleanupId, row.Kind, row.TargetId });
            builder.HasOne<DeletionCleanupRow>().WithMany().HasForeignKey(row => row.CleanupId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
