using TerraScale.Authentication.Features.Agents;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<AgentAssertionRecord> AgentAssertions = new(
        "agent_assertions",
        ["project_id", "agent_id", "jti"],
        ["project_id", "agent_id", "jti", "keep_until"],
        static row =>
        [
            row.ProjectId,
            row.AgentId,
            row.Jti,
            AuthDb.Instant(row.KeepUntil),
        ],
        static row => row.Version);

    public static readonly Table<AgentEnrollmentRecord> AgentEnrollments = new(
        "agent_enrollments",
        ["project_id", "id"],
        ["project_id", "id", "agent_id", "allow_self_subject", "created_at", "expires_at", "kid", "last_poll_at", "last_proof_issued_at", "name", "public_jwk", "requested_scopes", "status", "thumbprint", "user_code_hash", "user_id"],
        static row =>
        [
            row.ProjectId,
            row.Id,
            row.AgentId,
            row.AllowSelfSubject,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.ExpiresAt),
            row.Kid,
            AuthDb.Instant(row.LastPollAt),
            row.LastProofIssuedAt,
            row.Name,
            row.PublicJwk,
            AuthDb.Json(row.RequestedScopes, StorageJson.Default.IReadOnlyListString),
            row.Status,
            row.Thumbprint,
            row.UserCodeHash,
            row.UserId,
        ],
        static row => row.Version);

    public static readonly Table<AgentKeyRecord> AgentKeys = new(
        "agent_keys",
        ["project_id", "kid"],
        ["project_id", "kid", "agent_id", "created_at", "public_jwk", "revoked_at", "thumbprint"],
        static row =>
        [
            row.ProjectId,
            row.Kid,
            row.AgentId,
            AuthDb.Instant(row.CreatedAt),
            row.PublicJwk,
            AuthDb.Instant(row.RevokedAt),
            row.Thumbprint,
        ],
        static row => row.Version);

    public static readonly Table<AgentPolicyRecord> AgentPolicies = new(
        "agent_policies",
        ["project_id"],
        ["project_id", "allowed_scopes"],
        static row =>
        [
            row.ProjectId,
            AuthDb.Json(row.AllowedScopes, StorageJson.Default.IReadOnlyListString),
        ],
        static row => row.Version);

    public static readonly Table<AgentRecord> Agents = new(
        "agents",
        ["project_id", "id"],
        ["project_id", "id", "allow_self_subject", "allowed_scopes", "created_at", "expires_at", "last_used_at", "name", "owner_auth_epoch", "owner_auth_stamp", "owner_user_id", "revoked_at", "share_owner_email", "status"],
        static row =>
        [
            row.ProjectId,
            row.Id,
            row.AllowSelfSubject,
            AuthDb.Json(row.AllowedScopes, StorageJson.Default.IReadOnlyListString),
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.ExpiresAt),
            AuthDb.Instant(row.LastUsedAt),
            row.Name,
            row.OwnerAuthEpoch,
            row.OwnerAuthStamp,
            row.OwnerUserId,
            AuthDb.Instant(row.RevokedAt),
            row.ShareOwnerEmail,
            row.Status,
        ],
        static row => row.Version);
}
