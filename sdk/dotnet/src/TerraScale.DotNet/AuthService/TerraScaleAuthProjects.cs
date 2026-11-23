using System.Globalization;
using System.Net;
using System.Text.Json.Serialization.Metadata;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed partial class TerraScaleAuthProjectsClient(TerraScaleAuthClient client)
{
    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxExportCursorLength = 8192;
    private static TerraScaleAuthIntegrationJsonContext Json => TerraScaleAuthIntegrationJsonContext.Default;

    public Task<AuthResult<TerraScaleAuthProjectSettings>> GetSettingsAsync(
        string accessToken, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/settings", Json.TerraScaleAuthProjectSettings, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectSettings>> UpdateSettingsAsync(
        string accessToken, string projectId, TerraScaleAuthProjectSettingsUpdate request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/settings"),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectSettingsUpdate,
            Json.TerraScaleAuthProjectSettings, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectSettings>> SetProviderAsync(
        string accessToken, string projectId, string provider, TerraScaleAuthProjectProviderUpdate request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/providers/" + Provider(provider)),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectProviderUpdate,
            Json.TerraScaleAuthProjectSettings, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectSettings>> DeleteProviderAsync(
        string accessToken, string projectId, string provider, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, accessToken, projectId, "/providers/" + Provider(provider), Json.TerraScaleAuthProjectSettings, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>>> ListUsersAsync(
        string accessToken, string projectId, string? query = null, int? limit = null, string? after = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/users" + PageQuery("q", query, limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthUserExportPage>> ExportUsersAsync(
        string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        if (after is { Length: > MaxExportCursorLength })
            throw new ArgumentException("Export cursor must be at most 8192 characters.", nameof(after));
        return SendAsync(HttpMethod.Get, accessToken, projectId, "/users/export" + PageQuery("q", null, limit, after),
            Json.TerraScaleAuthUserExportPage, HttpStatusCode.OK, cancellationToken);
    }

    public Task<AuthResult<TerraScaleAuthProjectUser>> GetUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/users/" + Id(userId), Json.TerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectUser>> DisableUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, accessToken, projectId, $"/users/{Id(userId)}/disable", Json.TerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectUser>> EnableUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, accessToken, projectId, $"/users/{Id(userId)}/enable", Json.TerraScaleAuthProjectUser, HttpStatusCode.OK, cancellationToken);

    public Task<AuthOutcome> DeleteUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/users/" + Id(userId), cancellationToken);

    public Task<AuthOutcome> SignOutUserAsync(
        string accessToken, string projectId, string userId, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route(projectId, "/users/" + Id(userId) + "/sign-out"), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthProjectSession>>> ListSessionsAsync(
        string accessToken, string projectId, string? userId = null, int? limit = null, string? after = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/sessions" + PageQuery("user", userId is null ? null : Id(userId), limit, after),
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectSession, HttpStatusCode.OK, cancellationToken);

    public Task<AuthOutcome> RevokeSessionAsync(
        string accessToken, string projectId, string sessionId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/sessions/" + Id(sessionId), cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformPage<TerraScaleAuthProjectOAuthClient>>> ListClientsAsync(
        string accessToken, string projectId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, accessToken, projectId, "/clients",
            Json.TerraScaleAuthPlatformPageTerraScaleAuthProjectOAuthClient, HttpStatusCode.OK, cancellationToken);

    public Task<AuthResult<TerraScaleAuthProjectOAuthClient>> CreateClientAsync(
        string accessToken, string projectId, TerraScaleAuthProjectCreateOAuthClient request, string stepUpProof, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/clients"),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectCreateOAuthClient,
            Json.TerraScaleAuthProjectOAuthClient, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken, TerraScaleAuthPlatformClient.Proof(stepUpProof));

    public Task<AuthResult<TerraScaleAuthProjectOAuthClient>> UpdateProjectClientAsync(
        string accessToken, string projectId, string clientId, TerraScaleAuthProjectUpdateOAuthClient request, string stepUpProof, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/clients/" + Id(clientId)),
            request ?? throw new ArgumentNullException(nameof(request)), Json.TerraScaleAuthProjectUpdateOAuthClient,
            Json.TerraScaleAuthProjectOAuthClient, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken, TerraScaleAuthPlatformClient.Proof(stepUpProof));

    public Task<AuthResult<TerraScaleAuthProjectClientSecret>> RotateProjectClientSecretAsync(
        string accessToken, string projectId, string clientId, string stepUpProof, TerraScaleAuthProjectRotateOAuthClient? request = null, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/clients/" + Id(clientId) + "/secret/rotate"),
            request, Json.TerraScaleAuthProjectRotateOAuthClient,
            Json.TerraScaleAuthProjectClientSecret, TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken, TerraScaleAuthPlatformClient.Proof(stepUpProof));

    public Task<AuthOutcome> DeleteClientAsync(
        string accessToken, string projectId, string clientId, CancellationToken cancellationToken = default) =>
        DeleteAsync(accessToken, projectId, "/clients/" + Id(clientId), cancellationToken);

    private Task<AuthResult<TResponse>> SendAsync<TResponse>(HttpMethod method, string accessToken, string projectId, string relative,
        JsonTypeInfo<TResponse> responseType, HttpStatusCode expectedStatus, CancellationToken cancellationToken) =>
        client.SendAuthenticatedJsonAsync(method, Route(projectId, relative), null, Json.TerraScaleAuthPlatformName,
            responseType, TerraScaleAuthCredentials.Bearer(accessToken), expectedStatus, MaxRequestBytes, cancellationToken);

    private Task<AuthOutcome> DeleteAsync(string accessToken, string projectId, string relative, CancellationToken cancellationToken) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, Route(projectId, relative), null, Json.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);

    private static string Route(string projectId, string relative) => "/v1/" + Id(projectId) + relative;

    private static string Id(string value) => TerraScaleIds.Validate(value);

    private static string Provider(string value) => value is "google" or "github" or "apple" or "oidc" or "microsoft" or "gitlab" or "linkedin" or "slack" or "discord" or "facebook" or "x" or "oauth2"
        ? value : throw new ArgumentException("Provider must be google, github, apple, microsoft, gitlab, linkedin, slack, discord, facebook, x, oidc or oauth2.", nameof(value));

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
