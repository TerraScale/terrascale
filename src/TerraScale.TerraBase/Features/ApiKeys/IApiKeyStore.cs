using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.ApiKeys;

/// <summary>Scopes an API key can grant. Each scope is also the name of its authorization policy.</summary>
public static class ApiKeyScopes
{
    /// <summary>Read documents and list collections.</summary>
    public const string DocumentsRead = "documents:read";

    /// <summary>Create databases and write or delete documents.</summary>
    public const string DocumentsWrite = "documents:write";

    /// <summary>Every known scope.</summary>
    public static IReadOnlyList<string> All { get; } = [DocumentsRead, DocumentsWrite];
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

/// <summary>Issues and verifies scoped API keys. Only a hash of each secret is stored.</summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Issues a key for an organization, optionally narrowed to one of its projects and one database of that project,
    /// and returns it with its secret, which is never retrievable again. The caller checks that they exist.
    /// </summary>
    ValueTask<(ApiKey Key, string Secret)> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken);

    /// <summary>Finds the key for a presented secret; <see langword="null"/> when unknown or stored before tenancy.</summary>
    ValueTask<ApiKey?> FindAsync(string secret, CancellationToken cancellationToken);
}
