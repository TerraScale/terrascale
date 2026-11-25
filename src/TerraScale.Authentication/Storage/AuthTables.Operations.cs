using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<AccountDeletionReceipt> AccountDeletionReceipts = new(
        "account_deletion_receipts",
        ["instance_id", "token_hash"],
        ["instance_id", "token_hash", "expires_at", "generation", "organization_ids", "subject_id"],
        static row =>
        [
            row.InstanceId,
            row.TokenHash,
            AuthDb.Instant(row.ExpiresAt),
            row.Generation,
            AuthDb.Json(row.OrganizationIds, StorageJson.Default.IReadOnlyListString),
            row.SubjectId,
        ],
        null);

    public static readonly Table<ClosureCancellation> ClosureCancellations = new(
        "closure_cancellations",
        ["generation"],
        ["generation"],
        static row =>
        [
            row.Generation,
        ],
        null);

    public static readonly Table<ClosureCommit> ClosureCommits = new(
        "closure_commits",
        ["generation"],
        ["generation", "committed_at"],
        static row =>
        [
            row.Generation,
            AuthDb.Instant(row.CommittedAt),
        ],
        null);

    public static readonly Table<ClosureJob> ClosureJobs = new(
        "closure_jobs",
        ["instance_id", "scope", "subject_id"],
        ["instance_id", "scope", "subject_id", "account_id", "attempts", "committed_at", "failure_code", "fence_seen", "generation", "id", "next_attempt_at", "organization_id", "project_closure_version", "requested_at", "requested_by"],
        static row =>
        [
            row.InstanceId,
            row.Scope,
            row.SubjectId,
            row.AccountId,
            row.Attempts,
            AuthDb.Instant(row.CommittedAt),
            row.FailureCode,
            row.FenceSeen,
            row.Generation,
            row.Id,
            AuthDb.Instant(row.NextAttemptAt),
            row.OrganizationId,
            row.ProjectClosureVersion,
            AuthDb.Instant(row.RequestedAt),
            row.RequestedBy,
        ],
        static row => row.Version);

    public static readonly Table<DeletionCursorRecord> DeletionCursors = new(
        "deletion_cursors",
        ["name"],
        ["name", "after"],
        static row =>
        [
            row.Name,
            row.After,
        ],
        static row => row.Version);

    public static readonly Table<DeletionJob> DeletionJobs = new(
        "deletion_jobs",
        ["id"],
        ["id", "attempts", "auth_logical_verified_at", "closure_generation", "dashboard_verified_at", "due_at", "erasure_id", "failure_code", "fence_expires_at", "history_completed_at", "history_status", "instance_id", "next_attempt_at", "organization_id", "requested_at", "scope", "subject_id"],
        static row =>
        [
            row.Id,
            row.Attempts,
            AuthDb.Instant(row.AuthLogicalVerifiedAt),
            row.ClosureGeneration,
            AuthDb.Instant(row.DashboardVerifiedAt),
            AuthDb.Instant(row.DueAt),
            row.ErasureId,
            row.FailureCode,
            AuthDb.Instant(row.FenceExpiresAt),
            AuthDb.Instant(row.HistoryCompletedAt),
            row.HistoryStatus,
            row.InstanceId,
            AuthDb.Instant(row.NextAttemptAt),
            row.OrganizationId,
            AuthDb.Instant(row.RequestedAt),
            row.Scope,
            row.SubjectId,
        ],
        static row => row.Version);

    public static readonly Table<EmailBudgetRecord> EmailBudgets = new(
        "email_budgets",
        ["key"],
        ["key", "paused_until"],
        static row =>
        [
            row.Key,
            AuthDb.Instant(row.PausedUntil),
        ],
        static row => row.Version);

    public static readonly Table<EmailConfig> EmailConfigs = new(
        "email_configs",
        ["instance_id", "version"],
        ["instance_id", "version", "actor", "branding", "change", "created_at", "templates"],
        static row =>
        [
            row.InstanceId,
            row.Version,
            AuthDb.Json(row.Actor, EmailJson.Default.EmailActor),
            AuthDb.Json(row.Branding, EmailJson.Default.EmailBranding),
            row.Change,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Json(row.Templates, EmailJson.Default.IReadOnlyListEmailTemplateOverride),
        ],
        null);

    public static readonly Table<EmailDeliveryRecord> EmailDeliveries = new(
        "email_deliveries",
        ["kind", "key", "recipient"],
        ["kind", "key", "recipient", "lease_until", "status"],
        static row =>
        [
            row.Kind,
            row.Key,
            row.Recipient,
            AuthDb.Instant(row.LeaseUntil),
            row.Status,
        ],
        static row => row.Version);

    public static readonly Table<EmailReservationRecord> EmailReservations = new(
        "email_reservations",
        ["id"],
        ["id", "at", "attempt_id", "purpose", "tenant"],
        static row =>
        [
            row.Id,
            AuthDb.Instant(row.At),
            row.AttemptId,
            (long)row.Purpose,
            row.Tenant,
        ],
        null);

    public static readonly Table<MailRow> MailOutbox = new(
        "mail_outbox",
        ["id"],
        ["id", "attempts", "code_key", "created_at", "expires_at", "issue_id", "lease_until", "locale", "payload", "purpose", "state", "tenant"],
        static row =>
        [
            row.Id,
            row.Attempts,
            row.CodeKey,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.ExpiresAt),
            row.IssueId,
            AuthDb.Instant(row.LeaseUntil),
            row.Locale,
            row.Payload,
            (long?)row.Purpose,
            row.State,
            row.Tenant,
        ],
        static row => row.Version);

    public static readonly Table<RateLimitRecord> RateLimits = new(
        "rate_limits",
        ["instance_id", "key"],
        ["instance_id", "key", "used", "window"],
        static row =>
        [
            row.InstanceId,
            row.Key,
            row.Used,
            row.Window,
        ],
        static row => row.Version);

    public static readonly Table<SigningKey> SigningKeys = new(
        "signing_keys",
        ["instance_id", "id"],
        ["instance_id", "id", "activates_at", "algorithm", "created_at", "master_key_id", "public_metadata_mac", "secret_box", "x", "y"],
        static row =>
        [
            row.InstanceId,
            row.Id,
            row.ActivatesAt,
            row.Algorithm,
            row.CreatedAt,
            row.MasterKeyId,
            row.PublicMetadataMac,
            row.SecretBox,
            row.X,
            row.Y,
        ],
        static row => row.Version);

    public static readonly Table<SupportEmailDelivery> SupportDeliveries = new(
        "support_deliveries",
        ["request_id"],
        ["request_id", "acknowledgement_status", "instance_id", "status", "user_id"],
        static row =>
        [
            row.RequestId,
            row.AcknowledgementStatus,
            row.InstanceId,
            row.Status,
            row.UserId,
        ],
        static row => row.Version);
}
