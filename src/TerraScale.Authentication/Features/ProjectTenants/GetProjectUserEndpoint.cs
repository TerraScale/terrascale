using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class GetProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/users/{user}");
        this.Describe("get_project_user", "Get an end user", "Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return TypedResults.Json(Map(await this.Tenants.UserAsync(projectId, this.RouteId("user"), cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
