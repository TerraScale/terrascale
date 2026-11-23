using TerraScale.Authentication.Features.UserApiKeys;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<UserApiKeyPolicyRecord> UserApiKeyPolicies = new(
        "user_api_key_policies",
        ["project_id"],
        ["project_id", "allowed_scopes"],
        static row =>
        [
            row.ProjectId,
            AuthDb.Json(row.AllowedScopes, StorageJson.Default.IReadOnlyListString),
        ],
        static row => row.Version);

    public static readonly Table<UserApiKeyRecord> UserApiKeys = new(
        "user_api_keys",
        ["project_id", "id"],
        ["project_id", "id", "auth_epoch", "auth_stamp", "created_at", "expires_at", "name", "prefix", "scopes", "secret_hash", "user_id"],
        static row =>
        [
            row.ProjectId,
            row.Id,
            row.AuthEpoch,
            row.AuthStamp,
            AuthDb.Instant(row.CreatedAt),
            AuthDb.Instant(row.ExpiresAt),
            row.Name,
            row.Prefix,
            AuthDb.Json(row.Scopes, StorageJson.Default.IReadOnlyListString),
            row.SecretHash,
            row.UserId,
        ],
        static row => row.Version);
}
