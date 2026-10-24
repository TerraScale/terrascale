using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

/// <summary><c>GET /v1/terrascale/organizations/{organization}/sso</c>.</summary>
public sealed class GetSsoConnectionEndpoint(SsoConnections connections, PlatformDirectory platform, PlatformAuthentication authentication) : SsoEndpoint(platform, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Connection);
        this.Document("get_sso_connection", "Get the organization's SSO connection", "Needs the admin organization role. 404 when the organization has none. The client secret is never returned.", "Single sign-on", null, StatusCodes.Status200OK, typeof(SsoConnectionBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var organizationId = await this.OrganizationAsync(caller, Roles.Admin, cancellationToken);
        return this.Ok((await connections.ForOrganizationAsync(organizationId, cancellationToken) ?? throw PlatformDirectory.NotFound()).Value);
    }
}
