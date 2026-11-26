using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class DisableProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Post(Root + "/users/{user}/disable");
        this.Describe("disable_project_user", "Disable an end user", "Needs the admin project role. The user can no longer sign in, and every session and token it holds stops working at once.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return TypedResults.Json(Map(await this.Tenants.SetDisabledAsync(projectId, this.RouteId("user"), true, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
