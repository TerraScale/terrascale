using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.ApiKeys;

/// <summary>Bearer-token scopes. Each scope is also the name of its authorization policy.</summary>
public static class ApiKeyScopes
{
    /// <summary>Read documents and list collections.</summary>
    public const string DocumentsRead = "documents:read";

    /// <summary>Create databases and write or delete documents.</summary>
    public const string DocumentsWrite = "documents:write";

    /// <summary>Read shapes: CDN-cacheable snapshots and change logs of a collection and filter.</summary>
    public const string ShapesRead = "shapes:read";

    /// <summary>Read databases and database tooling.</summary>
    public const string DatabasesRead = "databases:read";

    /// <summary>Manage databases and database tooling.</summary>
    public const string DatabasesWrite = "databases:write";

    /// <summary>Scopes a legacy API key can grant.</summary>
    public static IReadOnlyList<string> All { get; } = [DocumentsRead, DocumentsWrite, ShapesRead];

    /// <summary>Additional scopes only ts-auth project access tokens carry, never legacy API keys.</summary>
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
public sealed record ApiKey(
    string Id, string Name, IReadOnlyList<string> Scopes, string OrganizationId, string? ProjectId = null, string? DatabaseId = null)
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

/// <summary>
/// A data API key of a project, as the dashboard lists it (an <see cref="ApiKey"/> of the project's organization narrowed
/// to the project). Never carries the secret or its hash.
/// </summary>
/// <param name="Id">Canonical uppercase ULID; list order uses a separate persisted sequence.</param>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Name">Label.</param>
/// <param name="Scopes">Granted scopes: <c>documents:read</c> and/or <c>documents:write</c>.</param>
/// <param name="DatabaseId">The only database of the project the key reaches; <see langword="null"/> for every database of the project.</param>
/// <param name="LastFour">The last four characters of the secret, to tell keys apart.</param>
/// <param name="CreatedAt">Creation time.</param>
public sealed record ProjectApiKey(
    string Id, string ProjectId, string Name, IReadOnlyList<string> Scopes, string? DatabaseId, string LastFour, DateTimeOffset CreatedAt);

/// <summary>A newly issued project key. <paramref name="Secret"/> is shown only in this response.</summary>
/// <param name="Key">The key.</param>
/// <param name="Secret">The bearer secret for <c>Authorization: Bearer</c>.</param>
public sealed record CreatedProjectApiKey(ProjectApiKey Key, string Secret);

/// <summary>A newly issued key with its secret, which is never retrievable again.</summary>
/// <param name="Key">The key.</param>
/// <param name="Secret">The bearer secret.</param>
public sealed record IssuedApiKey(ApiKey Key, string Secret);

/// <summary>An issued key, or why it was not issued: its scope does not exist.</summary>
public readonly union CreateApiKeyResult(IssuedApiKey, NotFound, Unavailable);

/// <summary>An issued project key with its secret, or why it was not issued.</summary>
public readonly union CreateProjectKeyResult(CreatedProjectApiKey, NotFound, Forbidden, Unavailable);

/// <summary>A page of a project's keys.</summary>
public readonly union ListProjectKeysResult(ManagementPage<ProjectApiKey>, NotFound, Unavailable);

/// <summary>A revoked project key, or why it was not revoked.</summary>
public readonly union RevokeProjectKeyResult(Done, NotFound, Forbidden, Unavailable);

/// <summary>Issues and verifies scoped API keys. Only a hash of each secret is stored.</summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Issues a key for an organization, optionally narrowed to one of its projects and one database of that project,
    /// and returns it with its secret, which is never retrievable again; <see cref="NotFound"/> when that scope does not
    /// exist, checked in the issuing write. A key narrowed to a project is listed with the project's keys, as if
    /// <see cref="CreateProjectKeyAsync"/> had issued it.
    /// </summary>
    ValueTask<CreateApiKeyResult> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken);

    /// <summary>Finds the key for a presented secret; <see cref="NotFound"/> when unknown or stored before tenancy.</summary>
    ValueTask<Found<ApiKey>> FindAsync(string secret, CancellationToken cancellationToken);

    /// <summary>
    /// Issues a key of a project for the acting user, optionally narrowed to one of its databases; not found when the user
    /// is not a member of the organization or either does not exist, forbidden for a viewer. Checked in the same catalog
    /// write as the issue.
    /// </summary>
    ValueTask<CreateProjectKeyResult> CreateProjectKeyAsync(
        string userId, string organizationId, string projectId, string name, IReadOnlyList<string> scopes, string? databaseId, CancellationToken cancellationToken);

    /// <summary>Lists the keys of a project in creation order.</summary>
    ValueTask<ListProjectKeysResult> ListProjectKeysAsync(
        string userId, string organizationId, string projectId, string? after, int limit, CancellationToken cancellationToken);

    /// <summary>Revokes a key of a project; it stops authenticating immediately. Forbidden for a viewer.</summary>
    ValueTask<RevokeProjectKeyResult> RevokeProjectKeyAsync(string userId, string organizationId, string projectId, string keyId, CancellationToken cancellationToken);
}
