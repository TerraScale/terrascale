using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed class AccountRow
{
    public required string Id { get; init; }

    public required string SubjectHash { get; init; }

    public string? UserId { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    public required string Phone { get; set; }

    public required string Locale { get; set; }

    public required bool OnboardingCompleted { get; set; }

    public required string OnboardingStep { get; set; }

    public required string OrganizationIds { get; set; }

    public required string PersonalOrganizationId { get; set; }

    public DateTime? PersonalClaimUntil { get; set; }

    public required string ActiveOrganizationId { get; set; }

    public required string ActiveProjectId { get; set; }

    public required DateTime CreatedAt { get; init; }

    public string? ErasureId { get; set; }

    public required string Version { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<AccountRow>
    {
        public void Configure(EntityTypeBuilder<AccountRow> builder)
        {
            builder.ToTable("accounts", table =>
            {
                table.HasCheckConstraint("ck_accounts_id", "length(id) = 26");
                table.HasCheckConstraint("ck_accounts_subject_hash", "length(subject_hash) = 64");
                table.HasCheckConstraint("ck_accounts_organization_ids", "json_type(organization_ids) = 'array'");
                table.HasCheckConstraint("ck_accounts_personal_organization_id", "length(personal_organization_id) IN (0, 26)");
                table.HasCheckConstraint("ck_accounts_erasure",
                    "(erasure_id IS NULL AND length(user_id) BETWEEN 1 AND 256) OR (length(erasure_id) = 26 AND user_id IS NULL AND name = '' AND email = '' AND phone = '' " +
                    "AND organization_ids = '[]' AND personal_organization_id = '' AND active_organization_id = '' AND active_project_id = '')");
                table.HasCheckConstraint("ck_accounts_version", "length(version) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.HasIndex(row => row.SubjectHash).IsUnique();
            builder.HasIndex(row => row.UserId).IsUnique();
            builder.Property(row => row.Version).IsConcurrencyToken();
        }
    }
}

public sealed class NotificationRow
{
    public required string AccountId { get; init; }

    public required string Id { get; init; }

    public required string OrganizationId { get; set; }

    public required string ProjectId { get; set; }

    public required string Type { get; set; }

    public required string BillingMonth { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    public required string Severity { get; set; }

    public required string Status { get; set; }

    public required string Url { get; set; }

    public required DateTime CreatedAt { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<NotificationRow>
    {
        public void Configure(EntityTypeBuilder<NotificationRow> builder)
        {
            builder.ToTable("notifications", table =>
            {
                table.HasCheckConstraint("ck_notifications_id", "length(id) BETWEEN 1 AND 128");
                table.HasCheckConstraint("ck_notifications_status", "lower(status) IN ('unread', 'read')");
            });
            builder.HasKey(row => new { row.AccountId, row.Id });
            builder.HasOne<AccountRow>().WithMany().HasForeignKey(row => row.AccountId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(row => new { row.OrganizationId, row.ProjectId });
        }
    }
}

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(InternalBillingPolicy))]
[JsonSerializable(typeof(Dictionary<string, InternalBillingAlert>))]
internal sealed partial class AccountRowJson : JsonSerializerContext;
