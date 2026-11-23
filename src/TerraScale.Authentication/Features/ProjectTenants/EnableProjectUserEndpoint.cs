using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class EnableProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/users/{user}/enable");
        this.Describe("enable_project_user", "Enable an end user", "Needs the admin project role. The user may sign in again; sessions ended by disabling stay ended.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return TypedResults.Json(Map(await this.Tenants.SetDisabledAsync(projectId, this.RouteId("user"), false, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
