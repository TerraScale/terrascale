using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<ChallengeRecord> Challenges = new(
        "challenges",
        ["instance_id", "purpose", "token_hash"],
        ["instance_id", "purpose", "token_hash", "consumed", "expires_at", "subject_id"],
        static row =>
        [
            row.InstanceId,
            row.Purpose,
            row.TokenHash,
            row.Consumed,
            AuthDb.Instant(row.ExpiresAt),
            row.SubjectId,
        ],
        static row => row.Version);

    public static readonly Table<CredentialClaimRecord> CredentialClaims = new(
        "credential_claims",
        ["instance_id", "kind", "key"],
        ["instance_id", "kind", "key", "owner_id"],
        static row =>
        [
            row.InstanceId,
            row.Kind,
            row.Key,
            row.OwnerId,
        ],
        static row => row.Version);

    public static readonly Table<EmailChangeRecord> EmailChanges = new(
        "email_changes",
        ["user_id"],
        ["user_id", "auth_epoch", "auth_stamp", "bindings", "cancel_hash", "code_hash", "expires_at", "failed_attempts", "instance_id", "new_email"],
        static row =>
        [
            row.UserId,
            row.AuthEpoch,
            row.AuthStamp,
            AuthDb.Json(row.Bindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            row.CancelHash,
            row.CodeHash,
            AuthDb.Instant(row.ExpiresAt),
            row.FailedAttempts,
            row.InstanceId,
            row.NewEmail,
        ],
        static row => row.Version);

    public static readonly Table<TotpCredentialRecord> MfaCredentials = new(
        "mfa_credentials",
        ["user_id"],
        ["user_id", "enabled", "enrollment_epoch", "enrollment_expires_at", "instance_id", "last_accepted_step", "recovery_expires_at", "recovery_hashes", "secret_box"],
        static row =>
        [
            row.UserId,
            row.Enabled,
            row.EnrollmentEpoch,
            AuthDb.Instant(row.EnrollmentExpiresAt),
            row.InstanceId,
            row.LastAcceptedStep,
            AuthDb.Instant(row.RecoveryExpiresAt),
            AuthDb.Json(row.RecoveryHashes, StorageJson.Default.IReadOnlyListString),
            row.SecretBox,
        ],
        static row => row.Version);

    public static readonly Table<PasscodeRecord> Passcodes = new(
        "passcodes",
        ["instance_id", "key"],
        ["instance_id", "key", "auth_epoch", "auth_stamp", "code_hash", "consumed", "email", "expires_at", "failed_attempts", "issuance_bindings", "issue_id", "issued_in_window", "password_hash", "pending_token_hash", "pruned", "purpose", "resets_authentication", "step_up_request_id", "user_id", "username", "window_started_at"],
        static row =>
        [
            row.InstanceId,
            row.Key,
            row.AuthEpoch,
            row.AuthStamp,
            row.CodeHash,
            row.Consumed,
            row.Email,
            AuthDb.Instant(row.ExpiresAt),
            row.FailedAttempts,
            AuthDb.Json(row.IssuanceBindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            row.IssueId,
            row.IssuedInWindow,
            row.PasswordHash,
            row.PendingTokenHash,
            row.Pruned,
            row.Purpose,
            row.ResetsAuthentication,
            row.StepUpRequestId,
            row.UserId,
            row.Username,
            AuthDb.Instant(row.WindowStartedAt),
        ],
        static row => row.Version);

    public static readonly Table<PasskeyCredentialEntry> Passkeys = new(
        "passkeys",
        ["instance_id", "credential_id"],
        ["instance_id", "credential_id", "aaguid", "created_at", "last_used_at", "name", "public_key_cose", "sign_count", "transports", "user_id"],
        static row =>
        [
            row.InstanceId,
            row.CredentialId,
            row.Aaguid,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.LastUsedAt),
            row.Name,
            row.PublicKeyCose,
            (long)row.SignCount,
            AuthDb.Json(row.Transports, StorageJson.Default.IReadOnlyListString),
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<ProviderLinkRecord> ProviderLinks = new(
        "provider_links",
        ["instance_id", "provider", "issuer", "client_id", "subject"],
        ["instance_id", "provider", "issuer", "client_id", "subject", "user_id"],
        static row =>
        [
            row.InstanceId,
            row.Provider,
            row.Issuer,
            row.ClientId,
            row.Subject,
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<SessionRecord> Sessions = new(
        "sessions",
        ["id"],
        ["id", "auth_epoch", "auth_stamp", "bindings", "created_at", "expires_at", "instance_id", "last_activity_at", "mfa", "mfa_method", "revoked", "session_stamp", "token_hash", "user_id", "verified_at"],
        static row =>
        [
            row.Id,
            row.AuthEpoch,
            row.AuthStamp,
            AuthDb.Json(row.Bindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.ExpiresAt),
            row.InstanceId,
            AuthDb.Instant(row.LastActivityAt),
            row.Mfa,
            row.MfaMethod,
            row.Revoked,
            row.SessionStamp,
            row.TokenHash,
            row.UserId,
            AuthDb.Instant(row.VerifiedAt),
        ],
        static row => row.Version);

    public static readonly Table<StepUpDeletionGrant> StepUpDeletionGrants = new(
        "step_up_deletion_grants",
        ["instance_id", "receipt_hash"],
        ["instance_id", "receipt_hash", "auth_epoch", "auth_stamp", "authorization_hash", "bindings", "expires_at", "user_id"],
        static row =>
        [
            row.InstanceId,
            row.ReceiptHash,
            row.AuthEpoch,
            row.AuthStamp,
            row.AuthorizationHash,
            AuthDb.Json(row.Bindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            AuthDb.Instant(row.ExpiresAt),
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<StepUpRecord> StepUpProofs = new(
        "step_up_proofs",
        ["id"],
        ["id", "approved", "audience", "auth_epoch", "auth_stamp", "bindings", "consumed", "expires_at", "instance_id", "mail_requested", "organization_id", "primary_verified", "purpose", "return_uri", "token_hash", "user_id"],
        static row =>
        [
            row.Id,
            row.Approved,
            row.Audience,
            row.AuthEpoch,
            row.AuthStamp,
            AuthDb.Json(row.Bindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            row.Consumed,
            AuthDb.Instant(row.ExpiresAt),
            row.InstanceId,
            row.MailRequested,
            row.OrganizationId,
            row.PrimaryVerified,
            row.Purpose,
            row.ReturnUri,
            row.TokenHash,
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<UserRecord> Users = new(
        "users",
        ["id"],
        ["id", "auth_epoch", "auth_stamp", "created_at", "custom_claims", "disabled", "email", "email_verified", "external_id", "first_name", "image_url", "instance_id", "is_anonymous", "last_name", "password_hash", "platform_role", "private_metadata", "public_metadata", "session_stamp", "unsafe_metadata", "username"],
        static row =>
        [
            row.Id,
            row.AuthEpoch,
            row.AuthStamp,
            AuthDb.Instant(row.CreatedAt),
            row.CustomClaims,
            row.Disabled,
            row.Email,
            row.EmailVerified,
            row.ExternalId,
            row.FirstName,
            row.ImageUrl,
            row.InstanceId,
            row.IsAnonymous,
            row.LastName,
            row.PasswordHash,
            row.PlatformRole,
            row.PrivateMetadata,
            row.PublicMetadata,
            row.SessionStamp,
            row.UnsafeMetadata,
            row.Username,
        ],
        static row => row.Version);
}
