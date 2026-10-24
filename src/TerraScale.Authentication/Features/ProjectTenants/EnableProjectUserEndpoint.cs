using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>POST /v1/{project}/users/{user}/enable</c>.</summary>
public sealed class EnableProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Root + "/users/{user}/enable");
        this.Document("enable_project_user", "Enable an end user", "Needs the admin project role. The user may sign in again; sessions ended by disabling stay ended.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(Map(await this.Tenants.SetDisabledAsync(projectId, this.RouteId("user"), false, cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
