using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class GetProjectSettingsEndpoint(TenantDirectory tenants, ProjectAuthSettings settings, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/settings");
        this.Describe("get_project_settings", "Get a project tenant's settings", "Its issuer, sign-in methods, branding and counts. Needs the viewer project role; a project the caller cannot see is 404.", "Project tenants", null, StatusCodes.Status200OK, typeof(SettingsBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return TypedResults.Json(await this.SettingsAsync(projectId, await settings.GetAsync(projectId, cancellationToken), cancellationToken), ProjectTenantJson.Default.SettingsBody);
    }
}
