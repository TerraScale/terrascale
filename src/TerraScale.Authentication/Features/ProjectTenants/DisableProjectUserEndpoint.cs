using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>POST /v1/{project}/users/{user}/disable</c>.</summary>
public sealed class DisableProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Root + "/users/{user}/disable");
        this.Document("disable_project_user", "Disable an end user", "Needs the admin project role. The user can no longer sign in, and every session and token it holds stops working at once.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(Map(await this.Tenants.SetDisabledAsync(projectId, this.RouteId("user"), true, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
