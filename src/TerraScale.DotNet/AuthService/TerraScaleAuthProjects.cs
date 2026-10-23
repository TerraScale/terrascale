using System.Globalization;
using System.Net;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>Project tenant management under <c>/v1/{project}</c> with the caller's own terrascale sign-in token.
/// ts-auth checks the caller's project role on every request: viewer reads, admin changes; an unknown or invisible
/// project answers 404 and a role too low answers 403 <c>forbidden</c>. Every request is sent once.</summary>
public sealed class TerraScaleAuthProjectsClient(TerraScaleAuthClient client)
{
    private const int MaxRequestBytes = 16 * 1024;
    private static TerraScaleAuthIntegrationJsonContext Json => TerraScaleAuthIntegrationJsonContext.Default;

    /// <summary>GET /v1/{project}/settings (viewer).</summary>
    public Task<TerraScaleAuthProjectSettings> GetSettingsAsync(
        string accessToken, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/settings", Json.TerraScaleAuthProjectSettings, HttpStatusCode.OK, cancellationToken);

    /// <summary>PUT /v1/{project}/settings (admin). Omitted parts stay unchanged; 400 <c>invalid_request</c> when
    /// both passcode and password would be off or branding is invalid.</summary>
    public Task<TerraScaleAuthProjectSettings> UpdateSettingsAsync(
        string accessToken, string projectId, TerraScaleAuthProjectSettingsUpdate request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/settings"),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectSettingsUpdate,
            Json.TerraScaleAuthProjectSettings, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);

    /// <summary>GET /v1/{project}/users (viewer). <paramref name="query"/> matches an email substring.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>> ListUsersAsync(
        string accessToken, string projectId, string? query = null, int? limit = null, string? after = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/users" + PageQuery("q", query, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    /// <summary>GET /v1/{project}/users/{user} (viewer).</summary>
    public Task<TerraScaleAuthProjectUser> GetUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/users/" + Id(userId), Json.TerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    /// <summary>POST /v1/{project}/users/{user}/disable (admin). Ends every session and token of the user.</summary>
    public Task<TerraScaleAuthProjectUser> DisableUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, accessToken, projectId, $"/users/{Id(userId)}/disable", Json.TerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    /// <summary>POST /v1/{project}/users/{user}/enable (admin).</summary>
    public Task<TerraScaleAuthProjectUser> EnableUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, accessToken, projectId, $"/users/{Id(userId)}/enable", Json.TerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    /// <summary>DELETE /v1/{project}/users/{user} (admin).</summary>
    public Task DeleteUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/users/" + Id(userId), cancellationToken);

    /// <summary>GET /v1/{project}/sessions (viewer). Live sessions only, optionally of one user.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectSession>> ListSessionsAsync(
        string accessToken, string projectId, string? userId = null, int? limit = null, string? after = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/sessions" + PageQuery("user", userId is null ? null : Id(userId), limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectSession, HttpStatusCode.OK, cancellationToken);

    /// <summary>DELETE /v1/{project}/sessions/{session} (admin).</summary>
    public Task RevokeSessionAsync(
        string accessToken, string projectId, string sessionId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/sessions/" + Id(sessionId), cancellationToken);

    /// <summary>GET /v1/{project}/clients (viewer). One page holding every client; Next is always null.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectOAuthClient>> ListClientsAsync(
        string accessToken, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/clients",
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectOAuthClient, HttpStatusCode.OK, cancellationToken);

    /// <summary>POST /v1/{project}/clients (admin). A confidential client's secret is returned once.</summary>
    public Task<TerraScaleAuthProjectOAuthClient> CreateClientAsync(
        string accessToken, string projectId, TerraScaleAuthProjectCreateOAuthClient request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/clients"),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectCreateOAuthClient,
            Json.TerraScaleAuthProjectOAuthClient, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken);

    /// <summary>DELETE /v1/{project}/clients/{client_id} (admin).</summary>
    public Task DeleteClientAsync(
        string accessToken, string projectId, string clientId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/clients/" + Id(clientId), cancellationToken);

    private Task<TResponse> SendAsync<TResponse>(HttpMethod method, string accessToken, string projectId, string relative,
        JsonTypeInfo<TResponse> responseType, HttpStatusCode expectedStatus, CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(method, Route(projectId, relative), null, Json.TerraScaleAuthPlatformName,
            responseType, TerraScaleAuthCredentials.Bearer(accessToken), expectedStatus, MaxRequestBytes, cancellationToken);

    private Task DeleteAsync(string accessToken, string projectId, string relative, CancellationToken cancellationToken) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, Route(projectId, relative), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

    private static string Route(string projectId, string relative) => "/v1/" + Id(projectId) + relative;

    private static string Id(string value) => TerraScaleIds.Validate(value);

    private static string PageQuery(string filterName, string? filter, int? limit, string? after)
    {
        if (filter is not null) ArgumentException.ThrowIfNullOrEmpty(filter);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Page limit must be 1 to 100.");
        if (after is not null) ArgumentException.ThrowIfNullOrEmpty(after);
        var parameters = new List<string>();
        if (filter is not null) parameters.Add(filterName + "=" + Uri.EscapeDataString(filter));
        if (limit is { } size) parameters.Add("limit=" + size.ToString(CultureInfo.InvariantCulture));
        if (after is not null) parameters.Add("after=" + Uri.EscapeDataString(after));
        return parameters.Count == 0 ? string.Empty : "?" + string.Join('&', parameters);
    }
}
