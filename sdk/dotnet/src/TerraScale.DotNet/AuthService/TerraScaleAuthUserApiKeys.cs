using System.Net;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>A user-owned opaque credential. Key is returned only at creation.</summary>
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
    /// <inheritdoc />
    public override string ToString() => nameof(TerraScaleAuthUserApiKey);
}

/// <summary>User credentials without their secrets.</summary>
public sealed record TerraScaleAuthUserApiKeyPage([property: JsonPropertyName("items")] IReadOnlyList<TerraScaleAuthUserApiKey> Items);

/// <summary>Scope policy for user-owned credentials.</summary>
public sealed record TerraScaleAuthUserApiKeyPolicy([property: JsonPropertyName("allowed_scopes")] IReadOnlyList<string> AllowedScopes);

/// <summary>A new user credential.</summary>
public sealed record TerraScaleAuthCreateUserApiKey(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

/// <summary>A secret to verify through the customer's backend.</summary>
public sealed record TerraScaleAuthVerifyUserApiKey([property: JsonPropertyName("key")] string Key)
{
    /// <inheritdoc />
    public override string ToString() => nameof(TerraScaleAuthVerifyUserApiKey);
}

/// <summary>Uncached verification result; identity and scopes are authoritative only when Valid is true.</summary>
public sealed record TerraScaleAuthUserApiKeyVerification(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

/// <summary>Management and backend verification of project end-user credentials.</summary>
public sealed partial class TerraScaleAuthProjectsClient
{
    /// <summary>Create a user-owned key with auth:write; the secret is returned once.</summary>
    public Task<TerraScaleAuthUserApiKey> CreateUserApiKeyAsync(string credential, string projectId, string userId, TerraScaleAuthCreateUserApiKey request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/users/" + TerraScaleIds.Validate(userId) + "/api-keys"),
            request, UserKeyJson.Default.TerraScaleAuthCreateUserApiKey, UserKeyJson.Default.TerraScaleAuthUserApiKey,
            TerraScaleAuthCredentials.Bearer(credential), HttpStatusCode.Created, MaxRequestBytes, cancellationToken);

    /// <summary>List a user's keys with auth:read.</summary>
    public Task<TerraScaleAuthUserApiKeyPage> ListUserApiKeysAsync(string credential, string projectId, string userId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, credential, projectId, "/users/" + TerraScaleIds.Validate(userId) + "/api-keys",
            UserKeyJson.Default.TerraScaleAuthUserApiKeyPage, HttpStatusCode.OK, cancellationToken);

    /// <summary>Revoke a user's key with auth:write.</summary>
    public Task RevokeUserApiKeyAsync(string credential, string projectId, string userId, string keyId, CancellationToken cancellationToken = default) =>
        DeleteAsync(credential, projectId, "/users/" + TerraScaleIds.Validate(userId) + "/api-keys/" + TerraScaleIds.Validate(keyId), cancellationToken);

    /// <summary>Verify a user key using an auth:read project key. Send only from a trusted backend.</summary>
    public Task<TerraScaleAuthUserApiKeyVerification> VerifyUserApiKeyAsync(string credential, string projectId, TerraScaleAuthVerifyUserApiKey request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route(projectId, "/api-keys/verify"),
            request, UserKeyJson.Default.TerraScaleAuthVerifyUserApiKey, UserKeyJson.Default.TerraScaleAuthUserApiKeyVerification,
            TerraScaleAuthCredentials.Bearer(credential), HttpStatusCode.OK, MaxRequestBytes, cancellationToken);

    /// <summary>Define the scopes an end user's keys may hold with auth:write.</summary>
    public Task<TerraScaleAuthUserApiKeyPolicy> SetUserApiKeyPolicyAsync(string credential, string projectId, TerraScaleAuthUserApiKeyPolicy request, CancellationToken cancellationToken = default) =>
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
