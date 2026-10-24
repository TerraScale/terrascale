using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>GET /v1/{project}/users/{user}</c>.</summary>
public sealed class GetProjectUserEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Root + "/users/{user}");
        this.Document("get_project_user", "Get an end user", "Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return Results.Json(Map(await this.Tenants.UserAsync(projectId, this.RouteId("user"), cancellationToken)), ProjectTenantJson.Default.ProjectUserBody);
    }
}
