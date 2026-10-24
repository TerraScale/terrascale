using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/sso</c>.</summary>
public sealed class PutSsoConnectionEndpoint(SsoConnections connections, PlatformDirectory platform, PlatformAuthentication authentication) : SsoEndpoint(platform, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(Connection);
        this.Document("put_sso_connection", "Create or change the organization's SSO connection", "Needs the owner role. A new domain starts unverified, with a fresh TXT verification value; the issuer's discovery document must name the same https issuer. client_secret may be omitted to keep the stored one. 409 conflict when another organization verified the domain or this organization has a connection for another domain.", "Single sign-on", typeof(SsoConnectionRequest), StatusCodes.Status200OK, typeof(SsoConnectionBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var organizationId = await this.OrganizationAsync(caller, Roles.Owner, cancellationToken);
        var request = await this.BodyAsync(SsoApiJson.Default.SsoConnectionRequest, cancellationToken);
        var domain = SsoConnections.Domain(request.Domain) ?? throw Invalid("domain must be an email domain such as example.com.");
        var role = request.DefaultRole ?? Roles.Member;
        if ((request.Protocol ?? SsoConnections.Oidc) != SsoConnections.Oidc || request.Oidc is not { } oidc)
        {
            throw Invalid("protocol must be oidc, with oidc set.");
        }

        if (role is not (Roles.Viewer or Roles.Member or Roles.Admin))
        {
            throw Invalid("default_role must be viewer, member or admin.");
        }

        if (oidc.Issuer is not { Length: <= 512 } issuer || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || oidc.ClientId is not { Length: >= 1 and <= 256 } clientId || clientId.Any(char.IsControl)
            || oidc.ClientSecret is { Length: 0 or > 1024 })
        {
            throw Invalid("oidc.issuer must be an https URL, oidc.client_id 1 to 256 characters and oidc.client_secret at most 1024.");
        }

        if (await connections.DiscoverAsync(issuer, cancellationToken) is null)
        {
            throw Invalid("The issuer's discovery document could not be read, or names another issuer.");
        }

        var secret = oidc.ClientSecret is { } sent ? connections.Seal(domain, sent) : null;
        return this.Ok(await connections.PutAsync(organizationId, caller.Id, domain, current => current with
        {
            Protocol = SsoConnections.Oidc,
            DefaultRole = role,
            Issuer = issuer,
            ClientId = clientId,
            ClientSecretBox = secret ?? current.ClientSecretBox ?? throw Invalid("oidc.client_secret is required."),
        }, cancellationToken));
    }
}
