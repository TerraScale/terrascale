using System.Globalization;
using System.Net;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed partial class TsAuthProjectsClient(TsAuthClient client)
    {
        private const int MaxRequestBytes = 16 * 1024;
        private const int MaxExportCursorLength = 8192;
        private static TsAuthIntegrationJsonContext Json => TsAuthIntegrationJsonContext.Default;
        public Task<TsAuthProjectSettings> GetSettingsAsync(
            string accessToken, string projectId, CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Get, accessToken, projectId, "/settings", Json.TsAuthProjectSettings, HttpStatusCode.OK, cancellationToken);
        }

        public Task<TsAuthProjectSettings> UpdateSettingsAsync(
            string accessToken, string projectId, TsAuthProjectSettingsUpdate request, CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/settings"),
                request ?? throw new ArgumentNullException(nameof(request)), Json.TsAuthProjectSettingsUpdate,
                Json.TsAuthProjectSettings, TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);
        }

        public Task<TsAuthProjectSettings> SetProviderAsync(
            string accessToken, string projectId, string provider, TsAuthProjectProviderUpdate request, CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/providers/" + Provider(provider)),
                request ?? throw new ArgumentNullException(nameof(request)), Json.TsAuthProjectProviderUpdate,
                Json.TsAuthProjectSettings, TsAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);
        }

        public Task<TsAuthProjectSettings> DeleteProviderAsync(
            string accessToken, string projectId, string provider, CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Delete, accessToken, projectId, "/providers/" + Provider(provider), Json.TsAuthProjectSettings, HttpStatusCode.OK, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthProjectUser>> ListUsersAsync(
            string accessToken, string projectId, string? query = null, int? limit = null, string? after = null,
            CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Get, accessToken, projectId, "/users" + PageQuery("q", query, limit, after),
                Json.TsAuthPlatformPageTsAuthProjectUser, HttpStatusCode.OK, cancellationToken);
        }

        public Task<TsAuthUserExportPage> ExportUsersAsync(
            string accessToken, string projectId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
        {
            if (after is { Length: > MaxExportCursorLength })
                throw new ArgumentException("Export cursor must be at most 8192 characters.", nameof(after));
            return SendAsync(HttpMethod.Get, accessToken, projectId, "/users/export" + PageQuery("q", null, limit, after),
                Json.TsAuthUserExportPage, HttpStatusCode.OK, cancellationToken);
        }

        public Task<TsAuthProjectUser> DisableUserAsync(
            string accessToken, string projectId, string userId, CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Post, accessToken, projectId, $"/users/{Id(userId)}/disable", Json.TsAuthProjectUser, HttpStatusCode.OK, cancellationToken);
        }

        public Task<TsAuthProjectUser> EnableUserAsync(
            string accessToken, string projectId, string userId, CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Post, accessToken, projectId, $"/users/{Id(userId)}/enable", Json.TsAuthProjectUser, HttpStatusCode.OK, cancellationToken);
        }

        public Task DeleteUserAsync(
            string accessToken, string projectId, string userId, CancellationToken cancellationToken = default)
        {
            return DeleteAsync(accessToken, projectId, "/users/" + Id(userId), cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthProjectSession>> ListSessionsAsync(
            string accessToken, string projectId, string? userId = null, int? limit = null, string? after = null,
            CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Get, accessToken, projectId, "/sessions" + PageQuery("user", userId is null ? null : Id(userId), limit, after),
                Json.TsAuthPlatformPageTsAuthProjectSession, HttpStatusCode.OK, cancellationToken);
        }

        public Task RevokeSessionAsync(
            string accessToken, string projectId, string sessionId, CancellationToken cancellationToken = default)
        {
            return DeleteAsync(accessToken, projectId, "/sessions/" + Id(sessionId), cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthProjectOAuthClient>> ListClientsAsync(
            string accessToken, string projectId, CancellationToken cancellationToken = default)
        {
            return SendAsync(HttpMethod.Get, accessToken, projectId, "/clients",
                Json.TsAuthPlatformPageTsAuthProjectOAuthClient, HttpStatusCode.OK, cancellationToken);
        }

        public Task<TsAuthProjectOAuthClient> CreateClientAsync(
            string accessToken, string projectId, TsAuthProjectCreateOAuthClient request, string stepUpProof, CancellationToken cancellationToken = default)
        {
            return client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/clients"),
                request ?? throw new ArgumentNullException(nameof(request)), Json.TsAuthProjectCreateOAuthClient,
                Json.TsAuthProjectOAuthClient, TsAuthCredentials.Bearer(accessToken), HttpStatusCode.Created, MaxRequestBytes, cancellationToken, TsAuthPlatformClient.Proof(stepUpProof));
        }

        public Task DeleteClientAsync(
            string accessToken, string projectId, string clientId, CancellationToken cancellationToken = default)
        {
            return DeleteAsync(accessToken, projectId, "/clients/" + Id(clientId), cancellationToken);
        }

        private Task<TResponse> SendAsync<TResponse>(HttpMethod method, string accessToken, string projectId, string relative,
            JsonTypeInfo<TResponse> responseType, HttpStatusCode expectedStatus, CancellationToken cancellationToken)
        {
            return client.SendAuthenticatedJsonAsync(method, Route(projectId, relative), null, Json.TsAuthPlatformName,
                responseType, TsAuthCredentials.Bearer(accessToken), expectedStatus, MaxRequestBytes, cancellationToken);
        }

        private Task DeleteAsync(string accessToken, string projectId, string relative, CancellationToken cancellationToken)
        {
            return client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, Route(projectId, relative), null, Json.TsAuthPlatformName,
                TsAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, MaxRequestBytes, cancellationToken);
        }

        private static string Route(string projectId, string relative)
        {
            return "/v1/" + Id(projectId) + relative;
        }

        private static string Id(string value)
        {
            return TerraScaleIds.Validate(value);
        }

        private static string Provider(string value)
        {
            return value is "google" or "github" or "apple" or "oidc"
            ? value : throw new ArgumentException("Provider must be google, github, apple or oidc.", nameof(value));
        }

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
}
