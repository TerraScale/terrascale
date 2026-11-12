using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Webhooks;

namespace TerraScale.Authentication.Storage;

public sealed partial class AuthDbContext
{
    public const string NameKeyProperty = "NameKey";

    public const string TargetProperty = "Target";

    public DbSet<OrganizationRecord> Organizations => this.Set<OrganizationRecord>();

    public DbSet<MembershipRecord> Memberships => this.Set<MembershipRecord>();

    public DbSet<ProjectRecord> Projects => this.Set<ProjectRecord>();

    public DbSet<ProjectMemberRecord> ProjectMembers => this.Set<ProjectMemberRecord>();

    public DbSet<ApiKeyRecord> ApiKeys => this.Set<ApiKeyRecord>();

    public DbSet<ApiKeyUsageRecord> ApiKeyUsage => this.Set<ApiKeyUsageRecord>();

    public DbSet<InvitationRecord> Invitations => this.Set<InvitationRecord>();

    public DbSet<AuditEventRecord> AuditEvents => this.Set<AuditEventRecord>();

    public DbSet<SsoConnectionRecord> SsoConnections => this.Set<SsoConnectionRecord>();

    public DbSet<ProjectAuthSettingsRecord> ProjectAuthSettings => this.Set<ProjectAuthSettingsRecord>();

    public DbSet<WebhookEndpointRecord> WebhookEndpoints => this.Set<WebhookEndpointRecord>();

    public DbSet<WebhookDeliveryRecord> WebhookDeliveries => this.Set<WebhookDeliveryRecord>();

    public DbSet<WebhookAttemptRecord> WebhookAttempts => this.Set<WebhookAttemptRecord>();

    public DbSet<WebhookOutboxRecord> WebhookOutbox => this.Set<WebhookOutboxRecord>();

    private static void ConfigurePlatform(ModelBuilder model)
    {
        model.Entity<OrganizationRecord>(organization =>
        {
            organization.ToTable("organizations", table => table.HasCheckConstraint("ck_organizations_counts", "owners >= 0 AND projects >= 0"));
            organization.HasKey(row => row.Id);
            organization.Property(row => row.Transfer).HasJson(PlatformStorageJson.Default.OwnershipTransferRecord);
            Versioned(organization);
        });

        model.Entity<MembershipRecord>(membership =>
        {
            membership.ToTable("memberships", table => table.HasCheckConstraint("ck_memberships_role", "role IS NULL OR role IN ('viewer', 'member', 'admin', 'owner')"));
            membership.HasKey(row => new { row.OrganizationId, row.UserId });
            membership.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            membership.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
            membership.HasIndex(row => row.UserId);
            Versioned(membership);
        });

        model.Entity<ProjectRecord>(project =>
        {
            project.ToTable("projects");
            project.HasKey(row => row.Id);
            project.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            project.Property<string>(NameKeyProperty).IsRequired();
            project.HasIndex(nameof(ProjectRecord.OrganizationId), NameKeyProperty).IsUnique();
            project.HasIndex(row => row.ClosureId).IsUnique();
            project.Property(row => row.AllowedCidrs).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(project);
        });

        model.Entity<ProjectMemberRecord>(grant =>
        {
            grant.ToTable("project_members", table => table.HasCheckConstraint("ck_project_members_role", "role IN ('viewer', 'member', 'admin')"));
            grant.HasKey(row => new { row.ProjectId, row.UserId });
            grant.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            grant.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            grant.HasOne<UserRecord>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
            grant.HasIndex(row => new { row.UserId, row.OrganizationId });
            Versioned(grant);
        });

        model.Entity<ApiKeyRecord>(key =>
        {
            key.ToTable("api_keys");
            key.HasKey(row => row.Id);
            key.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            key.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            key.HasOne<ApiKeyRecord>().WithMany().HasForeignKey(row => row.ParentKeyId).OnDelete(DeleteBehavior.Cascade);
            key.HasIndex(row => new { row.OrganizationId, row.ProjectId, row.Id });
            key.HasIndex(row => row.ParentKeyId);
            key.HasIndex(row => row.SecretHash).IsUnique();
            key.Property(row => row.Scopes).HasJson(StorageJson.Default.IReadOnlyListString);
            key.Property(row => row.AllowedCidrs).HasJson(StorageJson.Default.IReadOnlyListString);
            key.Property(row => row.SecretBindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            key.Property(row => row.PreviousSecretBindings).HasJson(StorageJson.Default.IReadOnlyListCredentialBinding);
            Versioned(key);
        });

        model.Entity<ApiKeyUsageRecord>(usage =>
        {
            usage.ToTable("api_key_usage");
            usage.HasKey(row => row.KeyId);
            usage.HasOne<ApiKeyRecord>().WithOne().HasForeignKey<ApiKeyUsageRecord>(row => row.KeyId).OnDelete(DeleteBehavior.Cascade);
            Versioned(usage);
        });

        model.Entity<InvitationRecord>(invitation =>
        {
            invitation.ToTable("invitations", table => table.HasCheckConstraint("ck_invitations_status", "status IN ('pending', 'accepted', 'declined', 'revoked', 'expired')"));
            invitation.HasKey(row => row.Id);
            invitation.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            invitation.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            invitation.Property<string>(TargetProperty).IsRequired();
            invitation.HasIndex(row => row.TokenHash).IsUnique();
            invitation.HasIndex(nameof(InvitationRecord.OrganizationId), TargetProperty, nameof(InvitationRecord.Email)).IsUnique().HasFilter("status = 'pending'");
            invitation.HasIndex(row => new { row.OrganizationId, row.ProjectId, row.Id });
            Versioned(invitation);
        });

        model.Entity<AuditEventRecord>(audit =>
        {
            audit.ToTable("audit_events");
            audit.HasKey(row => row.Id);
            audit.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            audit.HasIndex(row => new { row.OrganizationId, row.Id });
        });

        model.Entity<SsoConnectionRecord>(connection =>
        {
            connection.ToTable("sso_connections", table => table.HasCheckConstraint("ck_sso_connections_protocol", "protocol IN ('oidc', 'saml')"));
            connection.HasKey(row => row.Domain);
            connection.HasOne<OrganizationRecord>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            connection.HasIndex(row => row.OrganizationId).IsUnique();
            Versioned(connection);
        });

        model.Entity<ProjectAuthSettingsRecord>(settings =>
        {
            settings.ToTable("project_auth_settings", table =>
                table.HasCheckConstraint("ck_project_auth_settings_title_size", "title_size IS NULL OR title_size BETWEEN 20 AND 40"));
            settings.HasKey(row => row.InstanceId);
            settings.HasOne<ProjectRecord>().WithOne().HasForeignKey<ProjectAuthSettingsRecord>(row => row.InstanceId).OnDelete(DeleteBehavior.Cascade);
            settings.Property(row => row.Providers).HasJson(ProjectAuthSettingsJson.Default.IReadOnlyListSocialProviderRecord);
            settings.Property(row => row.FooterLinks).HasJson(ProjectAuthSettingsJson.Default.IReadOnlyListFooterLinkBody);
            settings.Property(row => row.SignUpMode).HasMaxLength(10);
            settings.Property(row => row.Restrictions).HasJson(ProjectTenantJson.Default.RestrictionsBody);
            settings.Property(row => row.Configuration).HasJson(ProjectAuthSettingsJson.Default.ConfigurationBody);
            settings.Property(row => row.SubscribedWebhookEvents).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(settings);
        });

        model.Entity<WebhookEndpointRecord>(endpoint =>
        {
            endpoint.ToTable("webhook_endpoints");
            endpoint.HasKey(row => row.Id);
            endpoint.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            endpoint.HasIndex(row => new { row.ProjectId, row.Id });
            endpoint.HasIndex(row => new { row.ProjectId, row.LastServedAt }).IsDescending(false, true);
            endpoint.HasIndex(row => new { row.DisabledAt, row.LeaseUntil });
            endpoint.Property(row => row.EventTypes).HasJson(StorageJson.Default.IReadOnlyListString);
            Versioned(endpoint);
        });

        model.Entity<WebhookDeliveryRecord>(delivery =>
        {
            delivery.ToTable("webhook_deliveries");
            delivery.HasKey(row => row.Id);
            delivery.HasOne<WebhookOutboxRecord>().WithMany().HasForeignKey(row => row.EventId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasOne<WebhookEndpointRecord>().WithMany().HasForeignKey(row => row.EndpointId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasIndex(row => new { row.EventId, row.EndpointId }).IsUnique();
            delivery.HasIndex(row => new { row.EndpointId, row.Status, row.NextAttemptAt });
            Versioned(delivery);
        });

        model.Entity<WebhookAttemptRecord>(attempt =>
        {
            attempt.ToTable("webhook_attempts");
            attempt.HasKey(row => row.Id);
            attempt.HasOne<WebhookDeliveryRecord>().WithMany().HasForeignKey(row => row.DeliveryId).OnDelete(DeleteBehavior.Cascade);
            attempt.HasIndex(row => new { row.DeliveryId, row.Id }).IsDescending(false, true);
            Versioned(attempt);
        });

        model.Entity<WebhookOutboxRecord>(outbox =>
        {
            outbox.ToTable("webhook_outbox", table =>
                table.HasCheckConstraint("ck_webhook_outbox_attempts", "attempts >= 0"));
            outbox.HasKey(row => row.EventId);
            outbox.HasOne<ProjectRecord>().WithMany().HasForeignKey(row => row.ProjectId).OnDelete(DeleteBehavior.Cascade);
            outbox.HasIndex(row => new { row.Status, row.NextAttemptAt });
            outbox.HasIndex(row => new { row.ProjectId, row.EventId });
            outbox.HasIndex(row => row.OccurredAt);
            Versioned(outbox);
        });
    }
}
