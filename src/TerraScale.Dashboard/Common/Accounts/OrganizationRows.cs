using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed class OrganizationRow
{
    public required string Id { get; init; }

    public required string Kind { get; set; }

    public required string Status { get; set; }

    public required string PlanId { get; set; }

    public required string BillingStatus { get; set; }

    public required bool PaymentMethodValidated { get; set; }

    public required long SpendCapCents { get; set; }

    public required bool PaidExcess { get; set; }

    public required bool Archived { get; set; }

    public required bool DeletionRequested { get; set; }

    public string? InternalBilling { get; set; }

    public required string InternalBillingAlerts { get; set; }

    public int? AuditRetentionDays { get; set; }

    public required string PendingProjectDeletions { get; set; }

    public required string DeletedProjectIds { get; set; }

    public string? ErasureId { get; set; }

    public required string Version { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<OrganizationRow>
    {
        public void Configure(EntityTypeBuilder<OrganizationRow> builder)
        {
            builder.ToTable("organizations", table =>
            {
                table.HasCheckConstraint("ck_organizations_id", "length(id) = 26");
                table.HasCheckConstraint("ck_organizations_kind", "kind IN ('personal', 'business')");
                table.HasCheckConstraint("ck_organizations_audit_retention", "audit_retention_days IS NULL OR audit_retention_days BETWEEN 1 AND 90");
                table.HasCheckConstraint("ck_organizations_project_markers",
                    "json_type(pending_project_deletions) = 'object' AND json_type(deleted_project_ids) = 'array' AND json_type(internal_billing_alerts) = 'object'");
                table.HasCheckConstraint("ck_organizations_erasure",
                    "erasure_id IS NULL OR (length(erasure_id) = 26 AND internal_billing IS NULL AND internal_billing_alerts = '{}' AND pending_project_deletions = '{}')");
                table.HasCheckConstraint("ck_organizations_version", "length(version) = 26");
            });
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Version).IsConcurrencyToken();
        }
    }
}

public sealed class OrganizationProjectRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string Regions { get; set; }

    public required string Status { get; set; }

    public required bool IsDefault { get; set; }

    public required long SpendCapCents { get; set; }

    public required long ReadLimit { get; set; }

    public required long WriteLimit { get; set; }

    public required long ReplicationLimit { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<OrganizationProjectRow>
    {
        public void Configure(EntityTypeBuilder<OrganizationProjectRow> builder)
        {
            builder.ToTable("organization_projects", table =>
            {
                table.HasCheckConstraint("ck_organization_projects_id", "length(id) = 26");
                table.HasCheckConstraint("ck_organization_projects_regions", "json_type(regions) = 'array'");
                table.HasCheckConstraint("ck_organization_projects_limits", "read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0");
            });
            builder.HasKey(row => row.Id);
            builder.HasOne<OrganizationRow>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}

public sealed class ProjectDatabaseRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string ProjectId { get; set; }

    public required string Name { get; set; }

    public string? PreviousName { get; set; }

    public string? ConflictOf { get; set; }

    public required string Region { get; set; }

    public required string Regions { get; set; }

    public required bool AllRegions { get; set; }

    public required long PlacementRevision { get; set; }

    public required string Endpoint { get; set; }

    public required string Status { get; set; }

    public required long SpendCapCents { get; set; }

    public required long ReadLimit { get; set; }

    public required long WriteLimit { get; set; }

    public required long ReplicationLimit { get; set; }

    public required int BackupRetentionDays { get; set; }

    public string? BranchedFrom { get; set; }

    public required DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<ProjectDatabaseRow>
    {
        public void Configure(EntityTypeBuilder<ProjectDatabaseRow> builder)
        {
            builder.ToTable("project_databases", table =>
            {
                table.HasCheckConstraint("ck_project_databases_id", "length(id) = 26");
                table.HasCheckConstraint("ck_project_databases_name", "length(name) > 0");
                table.HasCheckConstraint("ck_project_databases_regions", "json_type(regions) = 'array'");
                table.HasCheckConstraint("ck_project_databases_placement_revision", "placement_revision >= 0");
                table.HasCheckConstraint("ck_project_databases_limits", "read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0 AND backup_retention_days >= 0");
            });
            builder.HasKey(row => row.Id);
            builder.HasOne<OrganizationRow>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne<OrganizationProjectRow>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(row => new { row.ProjectId, row.Name }).IsUnique();
        }
    }
}

public sealed class AuditEventRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string Actor { get; set; }

    public required string Action { get; set; }

    public required string Target { get; set; }

    public required string TargetType { get; set; }

    public required DateTime OccurredAt { get; set; }

    public required string Source { get; set; }

    public string? Risk { get; set; }

    public string? Ip { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<AuditEventRow>
    {
        public void Configure(EntityTypeBuilder<AuditEventRow> builder)
        {
            builder.ToTable("audit_events", table =>
            {
                table.HasCheckConstraint("ck_audit_events_id", "length(id) = 26");
                table.HasCheckConstraint("ck_audit_events_action", "length(action) > 0");
            });
            builder.HasKey(row => row.Id);
            builder.HasOne<OrganizationRow>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(row => new { row.OrganizationId, row.OccurredAt });
        }
    }
}

public sealed class InvitationReservationRow
{
    public required string Id { get; init; }

    public required string OrganizationId { get; init; }

    public required string Email { get; set; }

    public required DateTime ReservedAt { get; set; }

    public required string InvitationId { get; set; }

    public string? ProjectId { get; set; }

    public required string Known { get; set; }

    public sealed class Configuration : IEntityTypeConfiguration<InvitationReservationRow>
    {
        public void Configure(EntityTypeBuilder<InvitationReservationRow> builder)
        {
            builder.ToTable("invitation_reservations", table =>
            {
                table.HasCheckConstraint("ck_invitation_reservations_id", "length(id) = 26");
                table.HasCheckConstraint("ck_invitation_reservations_email", "length(email) > 0");
                table.HasCheckConstraint("ck_invitation_reservations_known", "json_type(known) = 'array'");
            });
            builder.HasKey(row => row.Id);
            builder.HasOne<OrganizationRow>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(row => new { row.OrganizationId, row.InvitationId }).IsUnique().HasFilter("invitation_id <> ''");
        }
    }
}
