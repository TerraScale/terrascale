using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ListProjectInvitationsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, ProjectInvitations invitations) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/invitations");
        this.Describe("list_project_invitations", "List project invitations", "Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ProjectInvitationPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        return Results.Json(await invitations.ListAsync(projectId, limit, after, cancellationToken), ProjectTenantJson.Default.ProjectInvitationPageBody);
    }
}
