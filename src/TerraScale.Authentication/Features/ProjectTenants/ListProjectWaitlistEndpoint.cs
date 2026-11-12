using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ListProjectWaitlistEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, ProjectInvitations invitations) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/waitlist");
        this.Describe("list_project_waitlist", "List the project waitlist", "Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(WaitlistPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        return Results.Json(await invitations.WaitlistAsync(projectId, limit, after, cancellationToken), ProjectTenantJson.Default.WaitlistPageBody);
    }
}
