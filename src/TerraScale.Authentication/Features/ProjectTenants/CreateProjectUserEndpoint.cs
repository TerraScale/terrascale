using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
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

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(UserProfileJson.Default.CreateProjectUserRequest, cancellationToken);
        return await profiles.CreateAsync(project, request, cancellationToken) switch
        {
            UserRecord created => Results.Json(Map(created), ProjectTenantJson.Default.ProjectUserBody, statusCode: StatusCodes.Status201Created),
            Validation error => ErrorResults.ToResult(error),
            Conflict error => ErrorResults.ToResult(error),
        };
    }
}
