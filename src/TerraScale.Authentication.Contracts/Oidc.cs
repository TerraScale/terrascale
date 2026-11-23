using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

public sealed class OidcApplication
{
    public string? Id { get; set; }

    public string? InstanceId { get; set; }

    public string? ApplicationType { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public IReadOnlyList<CredentialBinding> SecretBindings { get; set; } = [];

    public string? PreviousSecretHash { get; set; }

    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }

    public string? ClientType { get; set; }

    public string? ConsentType { get; set; }

    public string? DisplayName { get; set; }

    public IReadOnlyList<string> Permissions { get; set; } = [];

    public IReadOnlyList<string> PostLogoutRedirectUris { get; set; } = [];

    public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } = new Dictionary<string, JsonElement>();

    public IReadOnlyList<string> RedirectUris { get; set; } = [];

    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];

    public IReadOnlyList<string> Requirements { get; set; } = [];

    public IReadOnlyDictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();

    [JsonIgnore]
    public string? Version { get; set; }
}

public sealed class OidcAuthorization
{
    public string? Id { get; set; }

    public string? InstanceId { get; set; }

    public string? ApplicationId { get; set; }

    public DateTimeOffset? CreationDate { get; set; }

    public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } = new Dictionary<string, JsonElement>();

    public IReadOnlyList<string> Scopes { get; set; } = [];

    public string? Status { get; set; }

    public string? Subject { get; set; }

    public string? Type { get; set; }

    [JsonIgnore]
    public string? Version { get; set; }
}

public sealed class OidcToken
{
    public IReadOnlyList<CredentialBinding> Bindings { get; set; } = [];

    public string? Id { get; set; }

    public string? InstanceId { get; set; }

    public string? ApplicationId { get; set; }

    public string? AuthorizationId { get; set; }

    public DateTimeOffset? CreationDate { get; set; }

    public DateTimeOffset? ExpirationDate { get; set; }

    public string? Payload { get; set; }

    public IReadOnlyDictionary<string, JsonElement> Properties { get; set; } = new Dictionary<string, JsonElement>();

    public DateTimeOffset? RedemptionDate { get; set; }

    public string? ReferenceId { get; set; }

    public string? Status { get; set; }

    public string? Subject { get; set; }

    public string? Type { get; set; }

    [JsonIgnore]
    public string? Version { get; set; }

    [JsonIgnore]
    public string? StoredReferenceId { get; set; }
}
