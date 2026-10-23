using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>GET /v1/{project}/users</c>.</summary>
public sealed class ListProjectUsersEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Root + "/users");
        this.Document("list_project_users", "List end users", "q matches an email substring; limit is 1 to 100 (default 50) and after the previous page's next. Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        var page = await this.Tenants.UsersAsync(projectId, this.HttpContext.Request.Query["q"].ToString(), limit, after, cancellationToken);
        return Results.Json(new ProjectUserPageBody([.. page.Items.Select(Map)], page.Next), ProjectTenantJson.Default.ProjectUserPageBody);
    }
}
