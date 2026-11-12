using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class UpdateProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserProfiles profiles)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected override int MaximumBodyBytes => 32 * 1024;

    public override void Configure()
    {
        Patch(Root + "/users/{user}");
        this.Describe("update_project_user", "Update an end user", "Requires auth:write or the admin project role. Omitted fields are unchanged. Metadata uses JSON merge patch; each merged object is at most 8192 UTF-8 bytes.",
            "Project tenants", typeof(UserProfilePatch), StatusCodes.Status200OK, typeof(ProjectUserBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var patch = await this.BodyAsync(UserProfileJson.Default.UserProfilePatch, cancellationToken);
        return Results.Json(Map(await profiles.UpdateAsync(project, this.RouteId("user"), patch, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
