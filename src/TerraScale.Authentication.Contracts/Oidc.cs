using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

/// <summary>
/// An OpenID Connect client (collection <c>oidc_applications</c>, addressed by instance and client id). OpenIddict owns
/// the field values; <see cref="ClientSecret"/> is the PBKDF2 hash it produces, never the secret.
/// </summary>
public sealed class OidcApplication
{
    /// <summary>Equal to <see cref="ClientId"/>.</summary>
    public string? Id { get; set; }

    /// <summary>The owning instance.</summary>
    public string? InstanceId { get; set; }

    /// <summary><c>web</c> or <c>native</c>.</summary>
    public string? ApplicationType { get; set; }

    /// <summary>The public client identifier.</summary>
    public string? ClientId { get; set; }

    /// <summary>The hashed client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary><c>confidential</c> or <c>public</c>.</summary>
    public string? ClientType { get; set; }

    /// <summary>OpenIddict consent type.</summary>
    public string? ConsentType { get; set; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Granted endpoint, grant type, response type and scope permissions.</summary>
    public IReadOnlyList<string> Permissions { get; set; } = [];

    /// <summary>Exact post-logout redirect URIs.</summary>
    public IReadOnlyList<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Additional properties.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } = new Dictionary<string, JsonElement>();

    /// <summary>Exact authorization redirect URIs.</summary>
    public IReadOnlyList<string> RedirectUris { get; set; } = [];

    /// <summary>Requirements such as PKCE.</summary>
    public IReadOnlyList<string> Requirements { get; set; } = [];

    /// <summary>Per-client settings.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();

    /// <summary>The TerraBase version this copy was read at; conditional writes name it.</summary>
    [JsonIgnore]
    public string? Version { get; set; }
}

/// <summary>An authorization granted to a client: one per sign-in (collection <c>oidc_authorizations</c>).</summary>
public sealed class OidcAuthorization
{
    /// <summary>Canonical ULID.</summary>
    public string? Id { get; set; }

    /// <summary>The owning instance.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The client's <see cref="OidcApplication.Id"/>.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Additional properties.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } = new Dictionary<string, JsonElement>();

    /// <summary>Granted scopes.</summary>
    public IReadOnlyList<string> Scopes { get; set; } = [];

    /// <summary><c>valid</c> or <c>revoked</c>.</summary>
    public string? Status { get; set; }

    /// <summary>The user id.</summary>
    public string? Subject { get; set; }

    /// <summary><c>ad-hoc</c> or <c>permanent</c>.</summary>
    public string? Type { get; set; }

    /// <summary>The TerraBase version this copy was read at; conditional writes name it.</summary>
    [JsonIgnore]
    public string? Version { get; set; }
}

/// <summary>
/// A token entry (collection <c>oidc_tokens</c>). Status changes, such as redeeming a code or refresh token, are
/// conditional writes on <see cref="Version"/> locally; replicated winner claims select one usable branch across regions. A reference token's
/// <see cref="ReferenceId"/> is OpenIddict's SHA-256 digest of the bearer handle, and <see cref="Payload"/> is the
/// token encrypted under a key derived from the master key.
/// </summary>
public sealed class OidcToken
{
    /// <summary>Immutable proof ancestry checked against the regional replicated claims on every use.</summary>
    public IReadOnlyList<CredentialBinding> Bindings { get; set; } = [];

    /// <summary>Canonical ULID.</summary>
    public string? Id { get; set; }

    /// <summary>The owning instance.</summary>
    public string? InstanceId { get; set; }

    /// <summary>The client's <see cref="OidcApplication.Id"/>.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>The <see cref="OidcAuthorization.Id"/> this token belongs to.</summary>
    public string? AuthorizationId { get; set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Expiry.</summary>
    public DateTimeOffset? ExpirationDate { get; set; }

    /// <summary>The protected token, for reference tokens.</summary>
    public string? Payload { get; set; }

    /// <summary>Additional properties.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } = new Dictionary<string, JsonElement>();

    /// <summary>First redemption time.</summary>
    public DateTimeOffset? RedemptionDate { get; set; }

    /// <summary>Digest of the reference handle.</summary>
    public string? ReferenceId { get; set; }

    /// <summary><c>valid</c>, <c>redeemed</c>, <c>revoked</c>, <c>rejected</c> or <c>inactive</c>.</summary>
    public string? Status { get; set; }

    /// <summary>The user id.</summary>
    public string? Subject { get; set; }

    /// <summary>The token type.</summary>
    public string? Type { get; set; }

    /// <summary>The TerraBase version this copy was read at; conditional writes name it.</summary>
    [JsonIgnore]
    public string? Version { get; set; }

    /// <summary>The reference id already persisted with its lookup claim, if any.</summary>
    [JsonIgnore]
    public string? StoredReferenceId { get; set; }
}
