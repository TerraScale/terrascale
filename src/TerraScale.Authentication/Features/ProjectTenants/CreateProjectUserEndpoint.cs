using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class CreateProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserProfiles profiles)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected override int MaximumBodyBytes => 32 * 1024;

    public override void Configure()
    {
        Post(Root + "/users");
        this.Describe("create_project_user", "Create an end user", "Requires auth:write or the admin project role. Password is optional. Does not send email.",
            "Project tenants", typeof(CreateProjectUserRequest), StatusCodes.Status201Created, typeof(ProjectUserBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(UserProfileJson.Default.CreateProjectUserRequest, cancellationToken);
        return Results.Json(Map(await profiles.CreateAsync(project, request, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody,
            statusCode: StatusCodes.Status201Created);
    }
}
