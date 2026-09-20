using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// OAuth client registration for this instance. Requests serialize snake_case
/// while responses are camelCase on the wire; both are pinned per family.
/// </summary>
public sealed class TerraScaleAuthOAuthClientsClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Registers an OAuth client. <see cref="TerraScaleAuthCreatedOAuthClient.ClientSecret"/>
    /// is returned exactly once and is null for public clients and
    /// <c>token_endpoint_auth_method = "none"</c>.
    /// </summary>
    public Task<TerraScaleAuthCreatedOAuthClient> CreateAsync(
        string name,
        IReadOnlyList<string> redirectUris,
        bool isPublic = false,
        string? tokenEndpointAuthMethod = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(redirectUris);
        if (redirectUris.Count == 0)
        {
            throw new ArgumentException("At least one redirect URI is required.", nameof(redirectUris));
        }
        return client.SendJsonAsync<TerraScaleAuthCreateOAuthClient, TerraScaleAuthCreatedOAuthClient>(
            HttpMethod.Post,
            client.ManagementRoute("/oauth-clients"),
            new TerraScaleAuthCreateOAuthClient(name, redirectUris, isPublic, tokenEndpointAuthMethod),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateOAuthClient,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreatedOAuthClient,
            cancellationToken);
    }

    /// <summary>Lists every OAuth client registered in this instance.</summary>
    public Task<TerraScaleAuthOAuthClientList> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/oauth-clients"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthOAuthClientList,
            cancellationToken);

    /// <summary>Deletes one OAuth client by its <c>clt_...</c> record id, not the OAuth clientId string.</summary>
    public Task DeleteAsync(string clientId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(
            HttpMethod.Delete,
            client.ManagementRoute("/oauth-clients/" + TerraScaleAuthPaths.Escape(clientId)),
            cancellationToken);
}

/// <summary>A registered OAuth client; its secret is never included.</summary>
public sealed record TerraScaleAuthOAuthClient(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("redirectUris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("tokenEndpointAuthMethod")] string TokenEndpointAuthMethod,
    [property: JsonPropertyName("public")] bool PublicClient,
    [property: JsonPropertyName("createdAt")] long CreatedAt);

/// <summary>
/// A newly registered OAuth client. <see cref="ClientSecret"/> is returned
/// exactly once and is null for public clients and <c>none</c> auth.
/// </summary>
public sealed record TerraScaleAuthCreatedOAuthClient(
    [property: JsonPropertyName("client")] TerraScaleAuthOAuthClient Client,
    [property: JsonPropertyName("client_secret")] string? ClientSecret)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthCreatedOAuthClient).Name} {{ Client = {Client}, ClientSecret = [REDACTED] }}";
}

/// <summary>The OAuth clients registered in this instance.</summary>
public sealed record TerraScaleAuthOAuthClientList(
    [property: JsonPropertyName("clients")] IReadOnlyList<TerraScaleAuthOAuthClient> Clients);

internal sealed record TerraScaleAuthCreateOAuthClient(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("public")] bool Public,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);
