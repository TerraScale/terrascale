namespace TerraScale.DotNet;

/// <summary>
/// Client for project auth API key endpoints.
/// </summary>
public sealed class TerraScaleApiKeysClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleApiKeysClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists project auth API keys.
    /// </summary>
    public Task<TerraScaleApiKeyPage> ListAsync(
        string projectId,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        TerraScalePagination.ValidateLimit(limit);
        return client.SendAsync<TerraScaleApiKeyPage>(
            HttpMethod.Get,
            TerraScaleApiRoutes.ApiKeysList(projectId, cursor, limit),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a project auth API key. The secret is returned only by this response.
    /// </summary>
    public Task<TerraScaleCreatedApiKey> CreateAsync(
        string projectId,
        TerraScaleCreateApiKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleCreatedApiKey>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ApiKeysCreate(projectId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Rotates a project auth API key. The replacement secret is returned only by this response.
    /// </summary>
    public Task<TerraScaleRotatedApiKey> RotateAsync(
        string projectId,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        return client.SendAsync<TerraScaleRotatedApiKey>(
            HttpMethod.Post,
            TerraScaleApiRoutes.ApiKeyRotate(projectId, keyId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Revokes a project auth API key.
    /// </summary>
    public Task<TerraScaleRevokedApiKey> RevokeAsync(
        string projectId,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        return client.SendAsync<TerraScaleRevokedApiKey>(
            HttpMethod.Delete,
            TerraScaleApiRoutes.ApiKeyRevoke(projectId, keyId),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// Request body for creating a project auth API key.
/// </summary>
public sealed record TerraScaleCreateApiKeyRequest
{
    /// <summary>
    /// Display name for the API key.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// API key scopes.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>
    /// Optional database id this API key is scoped to.
    /// </summary>
    public string? DatabaseId { get; init; }

    /// <summary>
    /// Optional environment id this API key is scoped to.
    /// </summary>
    public string? EnvironmentId { get; init; }

    /// <summary>
    /// Optional expiration timestamp.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Project auth API key page.
/// </summary>
public sealed record TerraScaleApiKeyPage(
    IReadOnlyList<TerraScaleApiKey> ApiKeys,
    string NextCursor);

/// <summary>
/// Project auth API key metadata.
/// </summary>
public sealed record TerraScaleApiKey(
    string KeyId,
    string ProjectId,
    string Name,
    string Prefix,
    string LastFour,
    IReadOnlyList<string> Scopes,
    string? DatabaseId,
    string? EnvironmentId,
    string? CreatedByUserId,
    string CreatedByEmail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool IsActive);

/// <summary>
/// Result of creating a project auth API key.
/// </summary>
public sealed record TerraScaleCreatedApiKey(
    TerraScaleApiKey ApiKey,
    string Secret);

/// <summary>
/// Result of rotating a project auth API key.
/// </summary>
public sealed record TerraScaleRotatedApiKey(
    TerraScaleApiKey ApiKey,
    string Secret,
    bool Rotated);

/// <summary>
/// Result of revoking a project auth API key.
/// </summary>
public sealed record TerraScaleRevokedApiKey(
    string KeyId,
    bool Revoked);
