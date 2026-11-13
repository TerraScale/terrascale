using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth
{
    public sealed record TsAuthScopedKeyRequest(
        [property: JsonPropertyName("project")] string Project,
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("access")] string Access,
        [property: JsonPropertyName("ttl_seconds")] int TtlSeconds);

    public sealed record TsAuthScopedKey(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("access")] string Access,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
    {
        public override string ToString() => "Scoped API key [REDACTED]";
    }

    public sealed partial class TsAuthClient
    {
        public Task<TsAuthScopedKey> CreateScopedKeyAsync(string parentKey, TsAuthScopedKeyRequest request, CancellationToken cancellationToken)
        {
            var body = parentKey.StartsWith("sk_", StringComparison.Ordinal) ? parentKey[3..] : parentKey;
            return SendAuthenticatedJsonAsync(HttpMethod.Post, "/v1/terrascale/api-keys/self/scoped-keys", request,
                TsAuthIntegrationJsonContext.Default.TsAuthScopedKeyRequest, TsAuthIntegrationJsonContext.Default.TsAuthScopedKey,
                TsAuthCredentials.Basic(body[..26], parentKey), HttpStatusCode.Created, 4 * 1024, cancellationToken);
        }
    }
}
