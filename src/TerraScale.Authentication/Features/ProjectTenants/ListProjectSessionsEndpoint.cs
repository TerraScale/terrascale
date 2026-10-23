using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>GET /v1/{project}/sessions</c>.</summary>
public sealed class ListProjectSessionsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Root + "/sessions");
        this.Document("list_project_sessions", "List live sessions", "Unrevoked, unexpired sessions of enabled users, of one user when user is set; limit is 1 to 100 (default 50) and after the previous page's next. Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectSessionPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        var user = this.HttpContext.Request.Query["user"].ToString() is { Length: > 0 } text
            ? PlatformDirectory.IsId(text) ? text : throw Invalid("user must be a user id.")
            : null;
        var page = await this.Tenants.SessionsAsync(projectId, user, limit, after, cancellationToken);
        return Results.Json(new ProjectSessionPageBody([.. page.Items.Select(item => new ProjectSessionBody(item.Id, item.Session.UserId, item.Email, item.Session.CreatedAt, item.Session.ExpiresAt))], page.Next), ProjectTenantJson.Default.ProjectSessionPageBody);
    }
}
