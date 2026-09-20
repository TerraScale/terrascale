using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Enterprise SSO connections: OIDC and SAML creation, listing, updating,
/// and deletion. Secrets (<c>client_secret</c>, <c>x509_certificate_pem</c>)
/// are write-only and never returned by any response.
/// </summary>
public sealed class TerraScaleAuthConnectionsClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Creates an OIDC connection. The authorization, token, and userinfo
    /// endpoints default to the values discovered from <paramref name="issuerUrl"/>.
    /// </summary>
    public Task<TerraScaleAuthConnection> CreateOidcAsync(
        string name,
        string issuerUrl,
        string clientId,
        string clientSecret,
        string? organizationId = null,
        string? authorizationEndpoint = null,
        string? tokenEndpoint = null,
        string? userinfoEndpoint = null,
        string[]? scopes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        return client.SendJsonAsync<TerraScaleAuthCreateOidcConnection, TerraScaleAuthConnection>(
            HttpMethod.Post,
            client.ManagementRoute("/connections"),
            new TerraScaleAuthCreateOidcConnection(
                "oidc",
                name,
                organizationId,
                issuerUrl,
                clientId,
                clientSecret,
                scopes,
                authorizationEndpoint,
                tokenEndpoint,
                userinfoEndpoint),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateOidcConnection,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthConnection,
            cancellationToken);
    }

    /// <summary>
    /// Creates a SAML connection from the IdP entity id, SSO redirect URL,
    /// and signing certificate PEM.
    /// </summary>
    public Task<TerraScaleAuthConnection> CreateSamlAsync(
        string name,
        string idpEntityId,
        string ssoRedirectUrl,
        string x509CertificatePem,
        string? organizationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(idpEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ssoRedirectUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(x509CertificatePem);
        return client.SendJsonAsync<TerraScaleAuthCreateSamlConnection, TerraScaleAuthConnection>(
            HttpMethod.Post,
            client.ManagementRoute("/connections"),
            new TerraScaleAuthCreateSamlConnection("saml", name, organizationId, idpEntityId, ssoRedirectUrl, x509CertificatePem),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateSamlConnection,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthConnection,
            cancellationToken);
    }

    /// <summary>Lists every SSO connection in the instance ordered by creation time.</summary>
    public Task<TerraScaleAuthConnectionPage> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/connections"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthConnectionPage,
            cancellationToken);

    /// <summary>
    /// Updates one connection. Absent (null) fields are treated as unchanged;
    /// pass <paramref name="clientSecret"/> to replace the stored secret.
    /// </summary>
    public Task<TerraScaleAuthConnection> UpdateAsync(
        string connectionId,
        bool? enabled = null,
        string? name = null,
        string? clientSecret = null,
        CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthUpdateConnection, TerraScaleAuthConnection>(
            HttpMethod.Patch,
            ConnectionRoute(connectionId),
            new TerraScaleAuthUpdateConnection(enabled, name, clientSecret),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUpdateConnection,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthConnection,
            cancellationToken);

    /// <summary>Deletes one SSO connection.</summary>
    public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(HttpMethod.Delete, ConnectionRoute(connectionId), cancellationToken);

    private string ConnectionRoute(string connectionId) =>
        client.ManagementRoute("/connections/" + TerraScaleAuthPaths.Escape(connectionId));
}

/// <summary>An enterprise SSO connection. Secrets are write-only and never present in this view.</summary>
public sealed record TerraScaleAuthConnection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("org_id")] string? OrganizationId,
    [property: JsonPropertyName("issuer_url")] string? IssuerUrl,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string? UserinfoEndpoint,
    [property: JsonPropertyName("idp_entity_id")] string? IdpEntityId,
    [property: JsonPropertyName("sso_redirect_url")] string? SsoRedirectUrl,
    [property: JsonPropertyName("sp_entity_id")] string? SpEntityId,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>One page of the SSO connection list, ordered by creation time.</summary>
public sealed record TerraScaleAuthConnectionPage(
    [property: JsonPropertyName("data")] IReadOnlyList<TerraScaleAuthConnection> Data);

internal sealed record TerraScaleAuthCreateOidcConnection(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("org_id")] string? OrganizationId,
    [property: JsonPropertyName("issuer_url")] string IssuerUrl,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("scopes")] string[]? Scopes,
    [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string? UserinfoEndpoint);

internal sealed record TerraScaleAuthCreateSamlConnection(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("org_id")] string? OrganizationId,
    [property: JsonPropertyName("idp_entity_id")] string IdpEntityId,
    [property: JsonPropertyName("sso_redirect_url")] string SsoRedirectUrl,
    [property: JsonPropertyName("x509_certificate_pem")] string X509CertificatePem);

internal sealed record TerraScaleAuthUpdateConnection(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("client_secret")] string? ClientSecret);
