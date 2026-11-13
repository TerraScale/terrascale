using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthUserApiKey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

public sealed record TsAuthUserApiKeyPage([property: JsonPropertyName("items")] IReadOnlyList<TsAuthUserApiKey> Items);

public sealed partial class TsAuthProjectsClient
{
    public Task<TsAuthUserApiKeyPage> ListUserApiKeysAsync(string accessToken, string projectId, string userId, CancellationToken cancellationToken) =>
        this.SendAsync(HttpMethod.Get, accessToken, projectId, "/users/" + Id(userId) + "/api-keys", Json.TsAuthUserApiKeyPage, HttpStatusCode.OK, cancellationToken);

    public Task RevokeUserApiKeyAsync(string accessToken, string projectId, string userId, string keyId, CancellationToken cancellationToken) =>
        this.DeleteAsync(accessToken, projectId, "/users/" + Id(userId) + "/api-keys/" + Id(keyId), cancellationToken);
}
