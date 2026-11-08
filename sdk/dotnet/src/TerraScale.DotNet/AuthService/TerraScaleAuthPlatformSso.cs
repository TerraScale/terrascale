using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>The DNS TXT record that proves the organization owns the SSO email domain.</summary>
/// <param name="Type">The record type; today always <c>TXT</c>.</param>
/// <param name="Name">The record name, for example <c>_terrascale-sso.example.com</c>.</param>
/// <param name="Value">The record value to publish before verifying.</param>
public sealed record TerraScaleAuthPlatformSsoVerification(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>Names the organization's OpenID Connect identity provider. The client secret is never returned; <paramref name="ClientSecretSet"/> only reports whether one is stored.</summary>
/// <param name="Issuer">The provider's HTTPS issuer URL.</param>
/// <param name="ClientId">The registered client identifier.</param>
/// <param name="ClientSecretSet">Whether a client secret is stored.</param>
/// <param name="RedirectUri">The producer's redirect URI registered with the provider.</param>
public sealed record TerraScaleAuthPlatformSsoOidc(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret_set")] bool ClientSecretSet,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri);

/// <summary>The organization's single sign-on connection. Until <paramref name="DomainVerified"/> is true, members keep signing in with passwords and email codes.</summary>
/// <param name="OrganizationId">The owning organization's ULID.</param>
/// <param name="Domain">The verified email domain.</param>
/// <param name="DomainVerified">Whether the DNS TXT verification succeeded.</param>
/// <param name="VerifiedAt">When the domain was last verified, if ever.</param>
/// <param name="Verification">The pending DNS TXT record to publish.</param>
/// <param name="Protocol">The SSO protocol; today always <c>oidc</c>.</param>
/// <param name="DefaultRole">The role assigned to members signing in through SSO.</param>
/// <param name="Oidc">The registered identity provider; the client secret is never returned.</param>
/// <param name="CreatedAt">When the connection was created.</param>
/// <param name="UpdatedAt">When the connection last changed.</param>
public sealed record TerraScaleAuthPlatformSsoConnection(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("domain_verified")] bool DomainVerified,
    [property: JsonPropertyName("verified_at")] DateTimeOffset? VerifiedAt,
    [property: JsonPropertyName("verification")] TerraScaleAuthPlatformSsoVerification? Verification,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("default_role")] string DefaultRole,
    [property: JsonPropertyName("oidc")] TerraScaleAuthPlatformSsoOidc? Oidc,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

/// <summary>Carries the identity provider registration. <paramref name="ClientSecret"/> may be null to keep the stored secret; formatting never reveals it.</summary>
/// <param name="Issuer">The provider's HTTPS issuer URL.</param>
/// <param name="ClientId">The registered client identifier.</param>
/// <param name="ClientSecret">The client secret, written once and never echoed.</param>
public sealed record TerraScaleAuthPlatformSsoOidcRequest(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret)
{
    /// <summary>Formats the registration without the client secret.</summary>
    public override string ToString() => $"TerraScaleAuthPlatformSsoOidcRequest {{ Issuer = {Issuer}, ClientId = {ClientId}, ClientSecret = [REDACTED] }}";
}

/// <summary>Creates or changes the organization's SSO connection. Formatting never reveals the OIDC client secret.</summary>
/// <param name="Domain">The member email domain, normalized to lowercase without a trailing dot.</param>
/// <param name="Protocol">The SSO protocol; null or <c>oidc</c>.</param>
/// <param name="DefaultRole">The role for members signing in through SSO; null, <c>viewer</c>, <c>member</c> or <c>admin</c>.</param>
/// <param name="Oidc">The identity provider registration.</param>
public sealed record TerraScaleAuthPlatformSsoConnectionRequest(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("protocol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Protocol,
    [property: JsonPropertyName("default_role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultRole,
    [property: JsonPropertyName("oidc")] TerraScaleAuthPlatformSsoOidcRequest Oidc)
{
    /// <summary>Formats the request without the OIDC client secret.</summary>
    public override string ToString() => $"TerraScaleAuthPlatformSsoConnectionRequest {{ Domain = {Domain}, Protocol = {Protocol}, DefaultRole = {DefaultRole}, Oidc = {Oidc} }}";
}

public sealed partial class TerraScaleAuthPlatformClient
{
    /// <summary>get_sso_connection: GET …/organizations/{organization}/sso. Requires organization admin; answers <see cref="TerraScaleAuthException"/> with status 404 when the organization has none.</summary>
    public Task<TerraScaleAuthPlatformSsoConnection> GetSsoConnectionAsync(string accessToken, string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/sso",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformSsoConnection,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    /// <summary>put_sso_connection: PUT …/organizations/{organization}/sso. Requires organization owner; sent once. The client secret is written but never echoed; read the connection before deciding to repeat an interrupted call.</summary>
    public Task<TerraScaleAuthPlatformSsoConnection> PutSsoConnectionAsync(string accessToken, string organizationId,
        TerraScaleAuthPlatformSsoConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var domain = request.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length < 4 || domain.Length > 253 || !SsoDomainPattern().IsMatch(domain))
            throw new ArgumentException("The SSO domain must be a lowercase email domain such as example.com.", nameof(request));
        if (request.Protocol is not (null or "" or "oidc"))
            throw new ArgumentException("The SSO protocol must be oidc.", nameof(request));
        if (request.DefaultRole is not (null or "" or "viewer" or "member" or "admin"))
            throw new ArgumentException("The SSO default role must be viewer, member or admin.", nameof(request));
        var issuer = request.Oidc.Issuer.Trim();
        if (issuer.Length > 512 || !issuer.StartsWith("https://", StringComparison.Ordinal) || issuer.IndexOfAny(['@', '?', '#']) >= 0)
            throw new ArgumentException("The OIDC issuer must be an HTTPS URL without user info, query or fragment.", nameof(request));
        if (request.Oidc.ClientId is null || request.Oidc.ClientId.Length is < 1 or > 256 || request.Oidc.ClientId.Any(char.IsControl))
            throw new ArgumentException("The OIDC client ID must be 1 to 256 characters without control characters.", nameof(request));
        if (request.Oidc.ClientSecret?.Length > 1024)
            throw new ArgumentException("The OIDC client secret must be at most 1024 characters.", nameof(request));
        var oidc = request.Oidc with { Issuer = issuer };
        var body = request with { Domain = domain, Oidc = oidc };
        return client.SendAuthenticatedJsonAsync(HttpMethod.Put, $"/v1/terrascale/organizations/{Id(organizationId)}/sso",
            body, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformSsoConnectionRequest,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformSsoConnection,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);
    }

    /// <summary>verify_sso_domain: POST …/organizations/{organization}/sso/verify. Requires organization owner; until the TXT record carries the verification value, answers 409 domain_unverified.</summary>
    public Task<TerraScaleAuthPlatformSsoConnection> VerifySsoConnectionAsync(string accessToken, string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/sso/verify",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformSsoConnection,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    /// <summary>delete_sso_connection: DELETE …/organizations/{organization}/sso. Requires organization owner; sent once, never repeated.</summary>
    public Task DeleteSsoConnectionAsync(string accessToken, string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/sso",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken);

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex SsoDomainPattern();
}
