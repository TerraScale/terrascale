using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Storage;

public static partial class AuthTables
{
    public static readonly Table<OidcApplication> OidcApplications = new(
        "oidc_applications",
        ["instance_id", "id"],
        ["instance_id", "id", "allowed_origins", "application_type", "client_id", "client_secret", "client_type", "consent_type", "display_name", "permissions", "post_logout_redirect_uris", "previous_secret_expires_at", "previous_secret_hash", "properties", "redirect_uris", "requirements", "secret_bindings", "settings"],
        static row =>
        [
            row.InstanceId,
            row.Id,
            AuthDb.Json(row.AllowedOrigins, StorageJson.Default.IReadOnlyListString),
            row.ApplicationType,
            row.ClientId,
            row.ClientSecret,
            row.ClientType,
            row.ConsentType,
            row.DisplayName,
            AuthDb.Json(row.Permissions, StorageJson.Default.IReadOnlyListString),
            AuthDb.Json(row.PostLogoutRedirectUris, StorageJson.Default.IReadOnlyListString),
            AuthDb.Instant(row.PreviousSecretExpiresAt),
            row.PreviousSecretHash,
            AuthDb.Json(row.Properties, StorageJson.Default.IReadOnlyDictionaryStringJsonElement),
            AuthDb.Json(row.RedirectUris, StorageJson.Default.IReadOnlyListString),
            AuthDb.Json(row.Requirements, StorageJson.Default.IReadOnlyListString),
            AuthDb.Json(row.SecretBindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            AuthDb.Json(row.Settings, StorageJson.Default.IReadOnlyDictionaryStringString),
        ],
        static row => row.Version);

    public static readonly Table<OidcAuthorization> OidcAuthorizations = new(
        "oidc_authorizations",
        ["id"],
        ["id", "application_id", "creation_date", "instance_id", "properties", "scopes", "status", "subject", "type"],
        static row =>
        [
            row.Id,
            row.ApplicationId,
            AuthDb.Instant(row.CreationDate),
            row.InstanceId,
            AuthDb.Json(row.Properties, StorageJson.Default.IReadOnlyDictionaryStringJsonElement),
            AuthDb.Json(row.Scopes, StorageJson.Default.IReadOnlyListString),
            row.Status,
            row.Subject,
            row.Type,
        ],
        static row => row.Version);

    public static readonly Table<OidcToken> OidcTokens = new(
        "oidc_tokens",
        ["id"],
        ["id", "application_id", "authorization_id", "bindings", "creation_date", "expiration_date", "instance_id", "payload", "properties", "redemption_date", "reference_id", "status", "subject", "type"],
        static row =>
        [
            row.Id,
            row.ApplicationId,
            row.AuthorizationId,
            AuthDb.Json(row.Bindings, StorageJson.Default.IReadOnlyListCredentialBinding),
            AuthDb.Instant(row.CreationDate),
            AuthDb.Instant(row.ExpirationDate),
            row.InstanceId,
            row.Payload,
            AuthDb.Json(row.Properties, StorageJson.Default.IReadOnlyDictionaryStringJsonElement),
            AuthDb.Instant(row.RedemptionDate),
            row.ReferenceId,
            row.Status,
            row.Subject,
            row.Type,
        ],
        static row => row.Version);
}
