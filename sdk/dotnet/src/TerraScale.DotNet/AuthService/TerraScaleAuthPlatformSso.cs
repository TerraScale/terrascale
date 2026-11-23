using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

using TerraScale.DotNet.Errors;

namespace TerraScale.DotNet;

public sealed record TerraScaleAuthPlatformSsoVerification(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record TerraScaleAuthPlatformSsoOidc(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret_set")] bool ClientSecretSet,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri);

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

public sealed record TerraScaleAuthPlatformSsoOidcRequest(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientSecret)
{
    public override string ToString() => $"TerraScaleAuthPlatformSsoOidcRequest {{ Issuer = {Issuer}, ClientId = {ClientId}, ClientSecret = [REDACTED] }}";
}

public sealed record TerraScaleAuthPlatformSsoConnectionRequest(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("protocol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Protocol,
    [property: JsonPropertyName("default_role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultRole,
    [property: JsonPropertyName("oidc")] TerraScaleAuthPlatformSsoOidcRequest Oidc)
{
    public override string ToString() => $"TerraScaleAuthPlatformSsoConnectionRequest {{ Domain = {Domain}, Protocol = {Protocol}, DefaultRole = {DefaultRole}, Oidc = {Oidc} }}";
}

public sealed partial class TerraScaleAuthPlatformClient
{
    public Task<AuthResult<TerraScaleAuthPlatformSsoConnection>> GetSsoConnectionAsync(string accessToken, string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Get, $"/v1/terrascale/organizations/{Id(organizationId)}/sso",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformSsoConnection,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    public Task<AuthResult<TerraScaleAuthPlatformSsoConnection>> PutSsoConnectionAsync(string accessToken, string organizationId,
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

    public Task<AuthResult<TerraScaleAuthPlatformSsoConnection>> VerifySsoConnectionAsync(string accessToken, string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, $"/v1/terrascale/organizations/{Id(organizationId)}/sso/verify",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformSsoConnection,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.OK, 16 * 1024, cancellationToken);

    public Task<AuthOutcome> DeleteSsoConnectionAsync(string accessToken, string organizationId,
        CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Delete, $"/v1/terrascale/organizations/{Id(organizationId)}/sso",
            null, TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthPlatformName,
            TerraScaleAuthCredentials.Bearer(accessToken), HttpStatusCode.NoContent, 16 * 1024, cancellationToken);

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex SsoDomainPattern();
}
