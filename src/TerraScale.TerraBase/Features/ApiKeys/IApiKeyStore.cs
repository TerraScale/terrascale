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
public sealed record ApiKey(string Id, string Name, IReadOnlyList<string> Scopes);

/// <summary>Issues and verifies scoped API keys. Only a hash of each secret is stored.</summary>
public interface IApiKeyStore
{
    /// <summary>Issues a key and returns it with its secret, which is never retrievable again.</summary>
    ValueTask<(ApiKey Key, string Secret)> CreateAsync(string name, IReadOnlyList<string> scopes, CancellationToken cancellationToken);

    /// <summary>Finds the key for a presented secret; <see langword="null"/> when unknown.</summary>
    ValueTask<ApiKey?> FindAsync(string secret, CancellationToken cancellationToken);
}
