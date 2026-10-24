using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/sso</c>.</summary>
public sealed class DeleteSsoConnectionEndpoint(SsoConnections connections, PlatformDirectory platform, PlatformAuthentication authentication) : SsoEndpoint(platform, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Connection);
        this.Document("delete_sso_connection", "Delete the organization's SSO connection", "Needs the owner role. Members of the domain sign in with email codes and passwords again; their memberships stay.", "Single sign-on", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await connections.DeleteAsync(await this.OrganizationAsync(caller, Roles.Owner, cancellationToken), caller.Id, cancellationToken);
        return Results.NoContent();
    }
}
