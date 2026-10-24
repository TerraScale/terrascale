using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

/// <summary>A body that creates or changes an organization's SSO connection.</summary>
/// <param name="Domain">The members' email domain.</param>
/// <param name="Protocol"><c>oidc</c> (the default).</param>
/// <param name="DefaultRole">viewer, member (the default) or admin.</param>
/// <param name="Oidc">The OpenID Connect identity provider.</param>
public sealed record SsoConnectionRequest(
    [property: JsonPropertyName("domain")] string? Domain,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("default_role")] string? DefaultRole,
    [property: JsonPropertyName("oidc")] SsoOidcRequest? Oidc);

/// <summary>An OpenID Connect identity provider.</summary>
/// <param name="Issuer">Its https issuer, whose discovery document must name the same issuer.</param>
/// <param name="ClientId">The client registered for TerraScale.</param>
/// <param name="ClientSecret">Its secret; omit to keep the stored one.</param>
public sealed record SsoOidcRequest(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_secret")] string? ClientSecret);

/// <summary>An organization's SSO connection.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Domain">The email domain.</param>
/// <param name="DomainVerified">Whether the TXT record was found; only then does sign-in go through the identity provider.</param>
/// <param name="VerifiedAt">When it was verified.</param>
/// <param name="Verification">The DNS record that proves the domain.</param>
/// <param name="Protocol"><c>oidc</c>.</param>
/// <param name="DefaultRole">The role of members who join through the identity provider.</param>
/// <param name="Oidc">The OpenID Connect identity provider.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last change.</param>
public sealed record SsoConnectionBody(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("domain_verified")] bool DomainVerified,
    [property: JsonPropertyName("verified_at")] DateTimeOffset? VerifiedAt,
    [property: JsonPropertyName("verification")] SsoVerificationBody Verification,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("default_role")] string DefaultRole,
    [property: JsonPropertyName("oidc")] SsoOidcBody? Oidc,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

/// <summary>The DNS record to publish.</summary>
/// <param name="Type"><c>TXT</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="Value">Its value.</param>
public sealed record SsoVerificationBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>An OpenID Connect identity provider; the secret is never returned.</summary>
/// <param name="Issuer">Its issuer.</param>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecretSet">Whether a secret is stored.</param>
/// <param name="RedirectUri">The redirect URI to register at the identity provider.</param>
public sealed record SsoOidcBody(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_secret_set")] bool ClientSecretSet,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri);

/// <summary>An organization's SSO connection endpoint (<c>/v1/terrascale/organizations/{organization}/sso</c>).</summary>
public abstract class SsoEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <summary>The connection's route.</summary>
    protected const string Connection = Organization + "/sso";

    /// <summary>The route's organization, after checking the caller holds <paramref name="minimum"/> there.</summary>
    protected async Task<string> OrganizationAsync(UserRecord caller, string minimum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var organizationId = this.RouteId("organization");
        PlatformDirectory.RequireRole(await platform.OrganizationAccessAsync(caller.Id, organizationId, cancellationToken), minimum);
        return organizationId;
    }

    /// <summary>The connection as its organization's admins see it.</summary>
    protected IResult Ok(SsoConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var origin = this.HttpContext.RequestServices.GetRequiredService<AuthOptions>().PublicOrigin;
        return Results.Json(new SsoConnectionBody(connection.OrganizationId, connection.Domain, connection.VerifiedAt is not null, connection.VerifiedAt,
            new("TXT", $"{SsoConnections.RecordLabel}.{connection.Domain}", SsoConnections.RecordPrefix + connection.VerificationToken),
            connection.Protocol, connection.DefaultRole,
            connection.Protocol == SsoConnections.Oidc ? new(connection.Issuer, connection.ClientId, connection.ClientSecretBox is not null, new Uri(origin, SsoConnections.CallbackPath).AbsoluteUri) : null,
            connection.CreatedAt, connection.UpdatedAt), SsoApiJson.Default.SsoConnectionBody);
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SsoConnectionRequest))]
[JsonSerializable(typeof(SsoConnectionBody))]
internal sealed partial class SsoApiJson : JsonSerializerContext;
