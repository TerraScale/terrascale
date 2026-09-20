using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Management API key creation, listing, and revocation. The full key value
/// is returned exactly once, at creation.
/// </summary>
public sealed class TerraScaleAuthApiKeysClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Creates a management API key. <see cref="TerraScaleAuthCreatedApiKey.ApiKey"/>
    /// is the full <c>sk_..._...</c> bearer value returned exactly once; ts-auth
    /// stores only its hash and cannot show it again.
    /// </summary>
    public Task<TerraScaleAuthCreatedApiKey> CreateAsync(
        string name,
        string[]? scopes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return client.SendJsonAsync<TerraScaleAuthCreateApiKey, TerraScaleAuthCreatedApiKey>(
            HttpMethod.Post,
            client.ManagementRoute("/api-keys"),
            new TerraScaleAuthCreateApiKey(name, scopes),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateApiKey,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreatedApiKey,
            cancellationToken);
    }

    /// <summary>Lists the instance's management API keys; items never include the key value.</summary>
    public Task<TerraScaleAuthApiKeyPage> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/api-keys"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthApiKeyPage,
            cancellationToken);

    /// <summary>Revokes one management API key; it stops authenticating immediately.</summary>
    public Task DeleteAsync(string keyId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            client.ManagementRoute("/api-keys/" + TerraScaleAuthPaths.Escape(keyId)),
            cancellationToken);
}

/// <summary>A newly created management API key, including the one-time key value.</summary>
public sealed record TerraScaleAuthCreatedApiKey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("scopes")] string[] Scopes)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthCreatedApiKey).Name} {{ Id = {Id}, Name = {Name}, ApiKey = [REDACTED], Scopes = {Scopes.Length} }}";
}

/// <summary>A management API key record; the key value itself is never included.</summary>
public sealed record TerraScaleAuthApiKey(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scopes")] string[] Scopes,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("last_used_at")] long? LastUsedAt,
    [property: JsonPropertyName("revoked")] bool Revoked);

/// <summary>The instance's management API keys.</summary>
public sealed record TerraScaleAuthApiKeyPage(
    [property: JsonPropertyName("data")] IReadOnlyList<TerraScaleAuthApiKey> Data);

internal sealed record TerraScaleAuthCreateApiKey(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("scopes")] string[]? Scopes);
