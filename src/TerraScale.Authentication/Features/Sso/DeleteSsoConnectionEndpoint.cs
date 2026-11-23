using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Sso;

public sealed class DeleteSsoConnectionEndpoint(SsoConnections connections, PlatformDirectory platform, PlatformAuthentication authentication) : SsoEndpoint(platform, authentication)
{
    public override void Configure()
    {
        Delete(Connection);
        this.Describe("delete_sso_connection", "Delete the organization's SSO connection", "Needs the owner role. Members of the domain sign in with email codes and passwords again; their memberships stay.", "Single sign-on", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await connections.DeleteAsync(await this.OrganizationAsync(caller, Roles.Owner, cancellationToken), caller.Id, cancellationToken) switch
        {
            AuthCompleted => TypedResults.NoContent(),
            AuthNotFound failure => ErrorResults.ToResult(failure),
            AuthConflict failure => ErrorResults.ToResult(failure),
        };
}
