using System.Net;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthUserApiKey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("key")] string? Key = null)
{
    public override string ToString() => nameof(TerraScaleAuthUserApiKey);
}

public sealed record TerraScaleAuthUserApiKeyPage([property: JsonPropertyName("items")] IReadOnlyList<TerraScaleAuthUserApiKey> Items);

public sealed record TerraScaleAuthUserApiKeyPolicy([property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string> AllowedScopes);

public sealed record TerraScaleAuthCreateUserApiKey(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record TerraScaleAuthVerifyUserApiKey([property: JsonPropertyName("key")] string Key)
{
    public override string ToString() => nameof(TerraScaleAuthVerifyUserApiKey);
}

public sealed record TerraScaleAuthUserApiKeyVerification(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

public sealed partial class TerraScaleAuthProjectsClient
{
    public Task<AuthResult<TerraScaleAuthUserApiKey>> CreateUserApiKeyAsync(string credential, string projectId, string userId, TerraScaleAuthCreateUserApiKey request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/users/" + TerraScaleIds.Validate(userId) + "/api-keys"),
            request, UserKeyJson.Default.TerraScaleAuthCreateUserApiKey, UserKeyJson.Default.TerraScaleAuthUserApiKey,
            TerraScaleAuthCredentials.Bearer(credential), HttpStatusCode.Created, MaxRequestBytes, cancellationToken);

    public Task<AuthResult<TerraScaleAuthUserApiKeyPage>> ListUserApiKeysAsync(string credential, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, credential, projectId, "/users/" + TerraScaleIds.Validate(userId) + "/api-keys",
            UserKeyJson.Default.TerraScaleAuthUserApiKeyPage, HttpStatusCode.OK, cancellationToken);

    public Task<AuthOutcome> RevokeUserApiKeyAsync(string credential, string projectId, string userId, string keyId, CancellationToken cancellationToken = default) =>
        DeleteAsync(credential, projectId, "/users/" + TerraScaleIds.Validate(userId) + "/api-keys/" + TerraScaleIds.Validate(keyId), cancellationToken);

    public Task<AuthResult<TerraScaleAuthUserApiKeyVerification>> VerifyUserApiKeyAsync(string credential, string projectId, TerraScaleAuthVerifyUserApiKey request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/api-keys/verify"),
            request, UserKeyJson.Default.TerraScaleAuthVerifyUserApiKey, UserKeyJson.Default.TerraScaleAuthUserApiKeyVerification,
            TerraScaleAuthCredentials.Bearer(credential), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);

    public Task<AuthResult<TerraScaleAuthUserApiKeyPolicy>> SetUserApiKeyPolicyAsync(string credential, string projectId, TerraScaleAuthUserApiKeyPolicy request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Put, Route(projectId, "/user-api-key-policy"),
            request, UserKeyJson.Default.TerraScaleAuthUserApiKeyPolicy, UserKeyJson.Default.TerraScaleAuthUserApiKeyPolicy,
            TerraScaleAuthCredentials.Bearer(credential), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TerraScaleAuthUserApiKey))]
[JsonSerializable(typeof(TerraScaleAuthUserApiKeyPage))]
[JsonSerializable(typeof(TerraScaleAuthUserApiKeyPolicy))]
[JsonSerializable(typeof(TerraScaleAuthCreateUserApiKey))]
[JsonSerializable(typeof(TerraScaleAuthVerifyUserApiKey))]
[JsonSerializable(typeof(TerraScaleAuthUserApiKeyVerification))]
internal sealed partial class UserKeyJson : JsonSerializerContext;
