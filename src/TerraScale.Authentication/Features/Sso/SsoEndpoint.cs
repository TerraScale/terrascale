using System.Text.Json.Serialization;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

public sealed record SsoConnectionRequest(
    [property: JsonPropertyName("domain")] string? Domain,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("default_role")] string? DefaultRole,
    [property: JsonPropertyName("oidc")] SsoOidcRequest? Oidc);

public sealed record SsoOidcRequest(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_secret")] string? ClientSecret);

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

public sealed record SsoVerificationBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record SsoOidcBody(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_secret_set")] bool ClientSecretSet,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri);

public abstract class SsoEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    protected const string Connection = Organization + "/sso";

    protected async Task<string> OrganizationAsync(UserRecord caller, string minimum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var organizationId = this.RouteId("organization");
        PlatformDirectory.RequireRole(await platform.OrganizationAccessAsync(caller.Id, organizationId, cancellationToken), minimum);
        return organizationId;
    }

    protected IResult Ok(SsoConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var origin = this.HttpContext.RequestServices.GetRequiredService<AuthOptions>().PublicOrigin;
        return TypedResults.Json(new SsoConnectionBody(connection.OrganizationId, connection.Domain, connection.VerifiedAt is not null, connection.VerifiedAt,
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
