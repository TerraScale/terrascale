using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.ApiKeys;

/// <summary>Bearer-token scopes. Each scope is also the name of its authorization policy.</summary>
public static class ApiKeyScopes
{
    /// <summary>Read documents and list collections.</summary>
    public const string DocumentsRead = "documents:read";

    /// <summary>Write or delete collections and documents.</summary>
    public const string DocumentsWrite = "documents:write";

    /// <summary>Read databases and database tooling.</summary>
    public const string DatabasesRead = "databases:read";

    /// <summary>Manage databases and database tooling.</summary>
    public const string DatabasesWrite = "databases:write";

    /// <summary>Scopes an operator-issued tb_ key can hold.</summary>
    public static IReadOnlyList<string> All { get; } = [DocumentsRead, DocumentsWrite];

    /// <summary>Additional scopes only ts-auth access tokens carry; operator-issued tb_ keys cannot hold them.</summary>
    public static IReadOnlyList<string> ProjectTokenScopes { get; } = [DatabasesRead, DatabasesWrite];
}

/// <summary>A stored API key, without its secret.</summary>
/// <param name="Id">Stable public identifier.</param>
/// <param name="Name">Operator-chosen label.</param>
/// <param name="Scopes">Granted scopes.</param>
/// <param name="OrganizationId">
/// The organization (tenant) the key acts for; it never reaches another. Keys stored before tenancy have none and no
/// longer authenticate.
/// </param>
/// <param name="ProjectId">The only project of the organization the key reaches; <see langword="null"/> for all of them.</param>
/// <param name="DatabaseId">The only database of <paramref name="ProjectId"/> the key reaches; <see langword="null"/> for all of them.</param>
/// <param name="ExpiresAt">When the key stops authenticating; <see langword="null"/> for never.</param>
public sealed record ApiKey(
    string Id, string Name, IReadOnlyList<string> Scopes, string OrganizationId, string? ProjectId = null, string? DatabaseId = null, DateTimeOffset? ExpiresAt = null)
{
    /// <summary>The key that authenticated <paramref name="context"/>.</summary>
    public static ApiKey Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ApiKey>() ?? throw new InvalidOperationException("the request was not authenticated with an API key");
    }

    /// <summary>Whether this key reaches <paramref name="database"/>: same organization and, when the key is narrowed, the same project and database.</summary>
    public bool Reaches(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return string.Equals(database.OrganizationId, OrganizationId, StringComparison.Ordinal)
            && (ProjectId is null || string.Equals(database.ProjectId, ProjectId, StringComparison.Ordinal))
            && (DatabaseId is null || string.Equals(database.DatabaseId, DatabaseId, StringComparison.Ordinal));
    }
}

/// <summary>A newly issued key with its secret, which is never retrievable again.</summary>
/// <param name="Key">The key.</param>
/// <param name="Secret">The bearer secret.</param>
public sealed record IssuedApiKey(ApiKey Key, string Secret);

/// <summary>An issued key, or why it was not issued: its scope does not exist.</summary>
public readonly union CreateApiKeyResult(IssuedApiKey, NotFound, Unavailable);

/// <summary>A revoked operator key, or why it was not revoked.</summary>
public readonly union RevocationResult(Done, NotFound, Unavailable);

/// <summary>Issues and verifies scoped API keys. Only a hash of each secret is stored.</summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Issues a key for an organization, optionally narrowed to one of its projects and one database of that project,
    /// and returns it with its secret, which is never retrievable again; <see cref="NotFound"/> when that scope does not
    /// exist, checked in the issuing write. A key narrowed to a project blocks its deletion until revoked.
    /// </summary>
    ValueTask<CreateApiKeyResult> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null);

    /// <summary>Revokes an operator-issued key by id; it stops authenticating immediately.</summary>
    ValueTask<RevocationResult> RevokeAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>Finds the key for a presented secret; <see cref="NotFound"/> when unknown, expired or stored before tenancy.</summary>
    ValueTask<Found<ApiKey>> FindAsync(string secret, CancellationToken cancellationToken);
}
