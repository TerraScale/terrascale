using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Webhooks;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<ApiKeyUsageRecord> ApiKeyUsage = new(
        "api_key_usage",
        ["key_id"],
        ["key_id", "instance_id", "last_used_at", "organization_id", "project_id"],
        static row =>
        [
            row.KeyId,
            row.InstanceId,
            AuthDb.Instant(row.LastUsedAt),
            row.OrganizationId,
            row.ProjectId,
        ],
        static row => row.Version);

    public static readonly Table<ApiKeyRecord> ApiKeys = new(
        "api_keys",
        ["id"],
        ["id", "allowed_cidrs", "created_at", "created_by", "database", "expires_at", "instance_id", "name", "organization_id", "parent_generation", "parent_key_id", "previous_secret_bindings", "previous_secret_expires_at", "previous_secret_hash", "project_id", "scopes", "secret_bindings", "secret_hash"],
        static row =>
        [
            row.Id,
            AuthDb.Json(row.AllowedCidrs, StorageJson.Default.IReadOnlyListString),
            AuthDb.Instant(row.CreatedAt),
            row.CreatedBy,
            row.Database,
            AuthDb.Instant(row.ExpiresAt),
            row.InstanceId,
            row.Name,
            row.OrganizationId,
            row.ParentGeneration,
            row.ParentKeyId,
            AuthDb.Json(row.PreviousSecretBindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            AuthDb.Instant(row.PreviousSecretExpiresAt),
            row.PreviousSecretHash,
            row.ProjectId,
            AuthDb.Json(row.Scopes, StorageJson.Default.IReadOnlyListString),
            AuthDb.Json(row.SecretBindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            row.SecretHash,
        ],
        static row => row.Version);

    public static readonly Table<AuditEventRecord> AuditEvents = new(
        "audit_events",
        ["id"],
        ["id", "action", "actor", "detail", "instance_id", "occurred_at", "organization_id", "target"],
        static row =>
        [
            row.Id,
            row.Action,
            row.Actor,
            row.Detail,
            row.InstanceId,
            AuthDb.Instant(row.OccurredAt),
            row.OrganizationId,
            row.Target,
        ],
        null);

    public static readonly Table<InvitationRecord> Invitations = new(
        "invitations",
        ["id"],
        ["id", "created_at", "decided_at", "decided_by", "email", "expires_at", "instance_id", "invited_by", "organization_id", "project_id", "role", "status", "target", "token_hash"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.DecidedAt),
            row.DecidedBy,
            row.Email,
            AuthDb.Instant(row.ExpiresAt),
            row.InstanceId,
            row.InvitedBy,
            row.OrganizationId,
            row.ProjectId,
            row.Role,
            row.Status,
            row.ProjectId ?? "-",
            row.TokenHash,
        ],
        static row => row.Version);

    public static readonly Table<MembershipRecord> Memberships = new(
        "memberships",
        ["organization_id", "user_id"],
        ["organization_id", "user_id", "instance_id", "joined_at", "role"],
        static row =>
        [
            row.OrganizationId,
            row.UserId,
            row.InstanceId,
            AuthDb.Instant(row.JoinedAt),
            row.Role,
        ],
        static row => row.Version);

    public static readonly Table<OrganizationRecord> Organizations = new(
        "organizations",
        ["id"],
        ["id", "created_at", "created_by", "instance_id", "name", "owners", "projects", "require_mfa", "transfer"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            row.CreatedBy,
            row.InstanceId,
            row.Name,
            row.Owners,
            row.Projects,
            row.RequireMfa,
            AuthDb.Json(row.Transfer, PlatformStorageJson.Default.OwnershipTransferRecord),
        ],
        static row => row.Version);

    public static readonly Table<ProjectAuthSettingsRecord> ProjectAuthSettings = new(
        "project_auth_settings",
        ["instance_id"],
        ["instance_id", "attack_protection", "background_color", "configuration", "default_theme", "email_message", "footer_links", "logo_url", "magic_link", "name", "organization_picker", "passcode", "password", "providers", "restrictions", "sign_up_mode", "subscribed_webhook_events", "text_color", "theme_color", "title_size", "username"],
        static row =>
        [
            row.InstanceId,
            AuthDb.Json(row.AttackProtection, AttackProtectionJson.Default.AttackProtectionSettings),
            row.BackgroundColor,
            AuthDb.Json(row.Configuration, ProjectAuthSettingsJson.Default.ConfigurationBody),
            row.DefaultTheme,
            row.EmailMessage,
            AuthDb.Json(row.FooterLinks, ProjectAuthSettingsJson.Default.IReadOnlyListFooterLinkBody),
            row.LogoUrl,
            row.MagicLink,
            row.Name,
            row.OrganizationPicker,
            row.Passcode,
            row.Password,
            AuthDb.Json(row.Providers, ProjectAuthSettingsJson.Default.IReadOnlyListSocialProviderRecord),
            AuthDb.Json(row.Restrictions, ProjectTenantJson.Default.RestrictionsBody),
            row.SignUpMode,
            AuthDb.Json(row.SubscribedWebhookEvents, StorageJson.Default.IReadOnlyListString),
            row.TextColor,
            row.ThemeColor,
            row.TitleSize,
            row.Username,
        ],
        static row => row.Version);

    public static readonly Table<ProjectMemberRecord> ProjectMembers = new(
        "project_members",
        ["project_id", "user_id"],
        ["project_id", "user_id", "granted_at", "instance_id", "organization_id", "role"],
        static row =>
        [
            row.ProjectId,
            row.UserId,
            AuthDb.Instant(row.GrantedAt),
            row.InstanceId,
            row.OrganizationId,
            row.Role,
        ],
        static row => row.Version);

    public static readonly Table<ProjectRecord> Projects = new(
        "projects",
        ["id"],
        ["id", "allowed_cidrs", "closure_id", "created_at", "created_by", "instance_id", "name", "name_key", "organization_id"],
        static row =>
        [
            row.Id,
            AuthDb.Json(row.AllowedCidrs, StorageJson.Default.IReadOnlyListString),
            row.ClosureId,
            AuthDb.Instant(row.CreatedAt),
            row.CreatedBy,
            row.InstanceId,
            row.Name,
            row.Name.ToUpperInvariant(),
            row.OrganizationId,
        ],
        static row => row.Version);

    public static readonly Table<SsoConnectionRecord> SsoConnections = new(
        "sso_connections",
        ["domain"],
        ["domain", "certificate", "client_id", "client_secret_box", "created_at", "default_role", "idp_entity_id", "instance_id", "issuer", "organization_id", "protocol", "sso_url", "updated_at", "verification_token", "verified_at"],
        static row =>
        [
            row.Domain,
            row.Certificate,
            row.ClientId,
            row.ClientSecretBox,
            AuthDb.Instant(row.CreatedAt),
            row.DefaultRole,
            row.IdpEntityId,
            row.InstanceId,
            row.Issuer,
            row.OrganizationId,
            row.Protocol,
            row.SsoUrl,
            AuthDb.Instant(row.UpdatedAt),
            row.VerificationToken,
            AuthDb.Instant(row.VerifiedAt),
        ],
        static row => row.Version);

    public static readonly Table<SsoIdentityLinkRecord> SsoIdentityLinks = new(
        "sso_identity_links",
        ["domain", "issuer", "subject"],
        ["domain", "issuer", "subject", "connection_updated_at", "organization_id", "user_id"],
        static row =>
        [
            row.Domain,
            row.Issuer,
            row.Subject,
            AuthDb.Instant(row.ConnectionUpdatedAt),
            row.OrganizationId,
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<WebhookAttemptRecord> WebhookAttempts = new(
        "webhook_attempts",
        ["id"],
        ["id", "created_at", "delivery_id", "latency_ms", "project_id", "response_body", "response_code", "status"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            row.DeliveryId,
            row.LatencyMs,
            row.ProjectId,
            row.ResponseBody,
            row.ResponseCode,
            row.Status,
        ],
        static row => row.Version);

    public static readonly Table<WebhookDeliveryRecord> WebhookDeliveries = new(
        "webhook_deliveries",
        ["id"],
        ["id", "attempts", "cycle_attempts", "endpoint_id", "event_id", "next_attempt_at", "project_id", "started_at", "status"],
        static row =>
        [
            row.Id,
            row.Attempts,
            row.CycleAttempts,
            row.EndpointId,
            row.EventId,
            AuthDb.Instant(row.NextAttemptAt),
            row.ProjectId,
            AuthDb.Instant(row.StartedAt),
            row.Status,
        ],
        static row => row.Version);

    public static readonly Table<WebhookEndpointStateRecord> WebhookEndpointStates = new(
        "webhook_endpoint_states",
        ["endpoint_id"],
        ["endpoint_id", "disabled_at", "first_failure_at", "last_served_at", "lease_until", "project_id"],
        static row =>
        [
            row.EndpointId,
            AuthDb.Instant(row.DisabledAt),
            AuthDb.Instant(row.FirstFailureAt),
            AuthDb.Instant(row.LastServedAt),
            AuthDb.Instant(row.LeaseUntil),
            row.ProjectId,
        ],
        static row => row.Version);

    public static readonly Table<WebhookEndpointRecord> WebhookEndpoints = new(
        "webhook_endpoints",
        ["id"],
        ["id", "created_at", "event_types", "instance_id", "previous_secret_box", "previous_secret_expires_at", "project_id", "secret_box", "url"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Json(row.EventTypes, StorageJson.Default.IReadOnlyListString),
            row.InstanceId,
            row.PreviousSecretBox,
            AuthDb.Instant(row.PreviousSecretExpiresAt),
            row.ProjectId,
            row.SecretBox,
            row.Url,
        ],
        static row => row.Version);

    public static readonly Table<WebhookOutboxRecord> WebhookOutbox = new(
        "webhook_outbox",
        ["event_id"],
        ["event_id", "actor_id", "agent_id", "agent_key_id", "attempts", "email", "endpoint_id", "event_type", "instance_id", "next_attempt_at", "occurred_at", "organization_id", "organization_role", "project_id", "provider", "reason", "session_id", "status", "user_id"],
        static row =>
        [
            row.EventId,
            row.ActorId,
            row.AgentId,
            row.AgentKeyId,
            row.Attempts,
            row.Email,
            row.EndpointId,
            row.EventType,
            row.InstanceId,
            AuthDb.Instant(row.NextAttemptAt),
            AuthDb.Instant(row.OccurredAt),
            row.OrganizationId,
            row.OrganizationRole,
            row.ProjectId,
            row.Provider,
            row.Reason,
            row.SessionId,
            row.Status,
            row.UserId,
        ],
        static row => row.Version);
}
