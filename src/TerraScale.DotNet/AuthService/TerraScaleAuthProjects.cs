using System.Globalization;
using System.Net;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>Project tenant management under <c>/v1/{project}</c> with the caller's own terrascale sign-in token.
/// Trusted-backend end-user token issuance instead uses an explicit project API key.
/// For tenant management, ts-auth checks the caller's role: viewer reads, admin changes; an unknown or invisible
/// project answers 404 and a role too low answers 403 <c>forbidden</c>. Every request is sent once.</summary>
public sealed partial class TerraScaleAuthProjectsClient(TerraScaleAuthClient client)
{
    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxExportCursorLength = 8192;
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

    /// <summary>PUT /v1/{project}/providers/{provider} (admin). Provider is google, github, apple or oidc; 400
    /// <c>invalid_request</c> when a value is invalid or the first configuration has no secret.</summary>
    public Task<TerraScaleAuthProjectSettings> SetProviderAsync(
        string accessToken, string projectId, string provider, TerraScaleAuthProjectProviderUpdate request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/providers/" + Provider(provider)),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectProviderUpdate,
            Json.TerraScaleAuthProjectSettings, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);

    /// <summary>DELETE /v1/{project}/providers/{provider} (admin). Answers the settings without the provider.</summary>
    public Task<TerraScaleAuthProjectSettings> DeleteProviderAsync(
        string accessToken, string projectId, string provider, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, accessToken, projectId, "/providers/" + Provider(provider), Json.TerraScaleAuthProjectSettings, HttpStatusCode.OK, cancellationToken);

    /// <summary>GET /v1/{project}/users (viewer). <paramref name="query"/> matches an email substring.</summary>
    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>> ListUsersAsync(
        string accessToken, string projectId, string? query = null, int? limit = null, string? after = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/users" + PageQuery("q", query, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    /// <summary>GET /v1/{project}/users/export (admin): one bounded JSON page of the project's end-user directory.
    /// ts-auth checks the admin role and commits a <c>project.users_exported</c> audit event before answering
    /// <b>every</b> page; an audit failure returns no data. Pass the previous page's <see cref="TerraScaleAuthUserExportPage.Next"/>
    /// as <paramref name="after"/>; null Next means the export is complete. The scan uses the first page's
    /// <see cref="TerraScaleAuthUserExportPage.CreatedBefore"/> creation cutoff and is <b>not a transaction snapshot</b>:
    /// later accounts are excluded, deletions disappear and fields reflect each page's read. Save each page before
    /// persisting its cursor and deduplicate by user id. No credentials or sessions are exported. Sent once, never
    /// retried, because every request is audited.</summary>
    /// <param name="accessToken">The caller's terrascale sign-in token.</param>
    /// <param name="projectId">The project ULID.</param>
    /// <param name="limit">Page size, 1 to 100; null uses the producer default of 100.</param>
    /// <param name="after">The opaque cursor from the previous page, at most 8192 characters.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<TerraScaleAuthUserExportPage> ExportUsersAsync(
        string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        if (after is { Length: > MaxExportCursorLength })
            throw new ArgumentException("Export cursor must be at most 8192 characters.", nameof(after));
        return SendAsync(HttpMethod.Get, accessToken, projectId, "/users/export" + PageQuery("q", null, limit, after),
            Json.TerraScaleAuthUserExportPage, HttpStatusCode.OK, cancellationToken);
    }

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

    private static string Provider(string value) => value is "google" or "github" or "apple" or "oidc"
        ? value : throw new ArgumentException("Provider must be google, github, apple or oidc.", nameof(value));

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
