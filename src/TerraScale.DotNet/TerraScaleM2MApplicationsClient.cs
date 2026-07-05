using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Client for project machine-to-machine application endpoints.
/// </summary>
public sealed class TerraScaleM2MApplicationsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleM2MApplicationsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists project machine-to-machine applications.
    /// </summary>
    public Task<TerraScaleM2MApplicationPage> ListAsync(
        string projectId,
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        TerraScalePagination.ValidateLimit(limit);
        return client.SendAsync<TerraScaleM2MApplicationPage>(
            HttpMethod.Get,
            TerraScaleApiRoutes.M2MApplicationsList(projectId, cursor, limit),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a machine-to-machine application. The client secret is returned only by this response.
    /// </summary>
    public Task<TerraScaleCreatedM2MApplication> CreateAsync(
        string projectId,
        TerraScaleCreateM2MApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendAsync<TerraScaleCreatedM2MApplication>(
            HttpMethod.Post,
            TerraScaleApiRoutes.M2MApplicationsCreate(projectId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Rotates a machine-to-machine application secret. The replacement secret is returned only by this response.
    /// </summary>
    public Task<TerraScaleRotatedM2MApplication> RotateAsync(
        string projectId,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return client.SendAsync<TerraScaleRotatedM2MApplication>(
            HttpMethod.Post,
            TerraScaleApiRoutes.M2MApplicationRotate(projectId, clientId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Deletes a machine-to-machine application.
    /// </summary>
    public Task<TerraScaleDeletedM2MApplication> DeleteAsync(
        string projectId,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return client.SendAsync<TerraScaleDeletedM2MApplication>(
            HttpMethod.Delete,
            TerraScaleApiRoutes.M2MApplicationDelete(projectId, clientId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates an OAuth-style bearer token for a machine-to-machine application.
    /// </summary>
    public Task<TerraScaleM2MToken> CreateTokenAsync(
        string projectId,
        TerraScaleCreateM2MTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTokenRequest(projectId, request);
        return client.SendAnonymousJsonAsync<TerraScaleM2MToken>(
            HttpMethod.Post,
            TerraScaleApiRoutes.M2MTokenCreate(projectId),
            request,
            cancellationToken);
    }

    /// <summary>
    /// Creates a client-credentials bearer token for a machine-to-machine application.
    /// </summary>
    public Task<TerraScaleM2MToken> CreateTokenAsync(
        string projectId,
        string clientId,
        string clientSecret,
        string scope = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        return CreateTokenAsync(
            projectId,
            new TerraScaleCreateM2MTokenRequest("client_credentials", clientId.Trim(), clientSecret.Trim(), scope.Trim()),
            cancellationToken);
    }

    /// <summary>
    /// Creates an OAuth-style bearer token using an application/x-www-form-urlencoded token request.
    /// </summary>
    public Task<TerraScaleM2MToken> CreateTokenFormAsync(
        string projectId,
        TerraScaleCreateM2MTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTokenRequest(projectId, request);
        return client.SendAnonymousFormAsync<TerraScaleM2MToken>(
            HttpMethod.Post,
            TerraScaleApiRoutes.M2MTokenCreate(projectId),
            ToFormValues(request, includeClientCredentials: true),
            authorization: null,
            cancellationToken);
    }

    /// <summary>
    /// Creates a client-credentials bearer token using an application/x-www-form-urlencoded token request.
    /// </summary>
    public Task<TerraScaleM2MToken> CreateTokenFormAsync(
        string projectId,
        string clientId,
        string clientSecret,
        string scope = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        return CreateTokenFormAsync(
            projectId,
            new TerraScaleCreateM2MTokenRequest("client_credentials", clientId.Trim(), clientSecret.Trim(), scope.Trim()),
            cancellationToken);
    }

    /// <summary>
    /// Creates a client-credentials bearer token using HTTP Basic client credentials.
    /// </summary>
    public Task<TerraScaleM2MToken> CreateTokenWithBasicCredentialsAsync(
        string projectId,
        string clientId,
        string clientSecret,
        string scope = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        return client.SendAnonymousFormAsync<TerraScaleM2MToken>(
            HttpMethod.Post,
            TerraScaleApiRoutes.M2MTokenCreate(projectId),
            ToFormValues(new TerraScaleCreateM2MTokenRequest("client_credentials", string.Empty, string.Empty, scope.Trim()), includeClientCredentials: false),
            CreateBasicAuthorization(clientId, clientSecret),
            cancellationToken);
    }

    private static void ValidateTokenRequest(string projectId, TerraScaleCreateM2MTokenRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GrantType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientSecret);
    }

    private static KeyValuePair<string, string>[] ToFormValues(
        TerraScaleCreateM2MTokenRequest request,
        bool includeClientCredentials)
    {
        if (!includeClientCredentials)
        {
            return
            [
                new("grant_type", request.GrantType.Trim()),
                new("scope", request.Scope.Trim())
            ];
        }

        return
        [
            new("grant_type", request.GrantType.Trim()),
            new("client_id", request.ClientId.Trim()),
            new("client_secret", request.ClientSecret.Trim()),
            new("scope", request.Scope.Trim())
        ];
    }

    private static AuthenticationHeaderValue CreateBasicAuthorization(
        string clientId,
        string clientSecret)
    {
        var value = $"{Uri.EscapeDataString(clientId.Trim())}:{Uri.EscapeDataString(clientSecret.Trim())}";
        var parameter = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return new AuthenticationHeaderValue("Basic", parameter);
    }
}

/// <summary>
/// Request body for creating a machine-to-machine application.
/// </summary>
public sealed record TerraScaleCreateM2MApplicationRequest
{
    /// <summary>
    /// Display name for the application.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Application scopes.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>
    /// Optional database id this application is scoped to.
    /// </summary>
    public string? DatabaseId { get; init; }

    /// <summary>
    /// Optional environment id this application is scoped to.
    /// </summary>
    public string? EnvironmentId { get; init; }

    /// <summary>
    /// Optional expiration timestamp.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// OAuth-style machine-to-machine token request.
/// </summary>
public sealed record TerraScaleCreateM2MTokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>
/// Machine-to-machine application page.
/// </summary>
public sealed record TerraScaleM2MApplicationPage(
    IReadOnlyList<TerraScaleM2MApplication> Applications,
    string NextCursor);

/// <summary>
/// Machine-to-machine application metadata.
/// </summary>
public sealed record TerraScaleM2MApplication(
    string ClientId,
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
/// Result of creating a machine-to-machine application.
/// </summary>
public sealed record TerraScaleCreatedM2MApplication(
    TerraScaleM2MApplication Application,
    string ClientSecret);

/// <summary>
/// Result of rotating a machine-to-machine application.
/// </summary>
public sealed record TerraScaleRotatedM2MApplication(
    TerraScaleM2MApplication Application,
    string ClientSecret,
    bool Rotated);

/// <summary>
/// Result of deleting a machine-to-machine application.
/// </summary>
public sealed record TerraScaleDeletedM2MApplication(
    string ClientId,
    bool Revoked);

/// <summary>
/// OAuth-style machine-to-machine bearer token response.
/// </summary>
public sealed record TerraScaleM2MToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("database_id")] string? DatabaseId,
    [property: JsonPropertyName("environment_id")] string? EnvironmentId);
