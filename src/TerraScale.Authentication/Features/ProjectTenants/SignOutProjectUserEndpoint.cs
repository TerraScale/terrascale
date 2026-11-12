using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class SignOutProjectUserEndpoint(TenantDirectory tenants, Sessions sessions, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/users/{user}/sign-out");
        this.Describe("sign_out_project_user", "Sign a user out everywhere",
            "Needs the admin project role. Advances the user's authentication epoch and stamp, invalidating every hosted session, authorization code and refresh token. Existing offline access tokens remain valid until expiry.",
            "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await sessions.RevokeAllAsync(project, this.RouteId("user"), cancellationToken)
            ? Results.NoContent() : throw PlatformDirectory.NotFound();
    }
}
