using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ListProjectUsersEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Get(Root + "/users");
        this.Describe("list_project_users", "List end users", "q matches an email substring; limit is 1 to 100 (default 50) and after the previous page's next. Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectUserPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        var page = await this.Tenants.UsersAsync(projectId, this.HttpContext.Request.Query["q"].ToString(), limit, after, cancellationToken);
        return TypedResults.Json(new ProjectUserPageBody([.. page.Items.Select(Map)], page.Next), ProjectTenantJson.Default.ProjectUserPageBody);
    }
}
