using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.ApiKeys;

public static class ApiKeyScopes
{
    public const string SqlRead = "sql:read";

    public const string SqlWrite = "sql:write";

    public const string DatabasesRead = "databases:read";

    public const string DatabasesWrite = "databases:write";

    public static IReadOnlyList<string> All { get; } = [SqlRead, SqlWrite];

    public static IReadOnlyList<string> ProjectTokenScopes { get; } = [DatabasesRead, DatabasesWrite];
}

public sealed record ApiKey(
    string Id, string Name, IReadOnlyList<string> Scopes, string OrganizationId, string? ProjectId = null, string? DatabaseId = null, DateTimeOffset? ExpiresAt = null)
{
    public static ApiKey Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ApiKey>() ?? throw new InvalidOperationException("the request was not authenticated with an API key");
    }

    public bool Reaches(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return string.Equals(database.OrganizationId, OrganizationId, StringComparison.Ordinal)
            && (ProjectId is null || string.Equals(database.ProjectId, ProjectId, StringComparison.Ordinal))
            && (DatabaseId is null || string.Equals(database.DatabaseId, DatabaseId, StringComparison.Ordinal));
    }
}

public sealed record IssuedApiKey(ApiKey Key, string Secret);

public readonly union CreateApiKeyResult(IssuedApiKey, NotFound, Unavailable);

public readonly union RevocationResult(Done, NotFound, Unavailable);

public interface IApiKeyStore
{
    ValueTask<CreateApiKeyResult> CreateAsync(
        string name, IReadOnlyList<string> scopes, string organizationId, string? projectId, string? databaseId, CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null);

    ValueTask<RevocationResult> RevokeAsync(string keyId, CancellationToken cancellationToken);

    ValueTask<Found<ApiKey>> FindAsync(string secret, CancellationToken cancellationToken);
}
