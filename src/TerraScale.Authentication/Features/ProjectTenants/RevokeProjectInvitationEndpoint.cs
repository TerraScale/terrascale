using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class RevokeProjectInvitationEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, ProjectInvitations invitations) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Delete(Root + "/invitations/{invitation}");
        this.Describe("revoke_project_invitation", "Revoke a project invitation", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await invitations.RevokeAsync(projectId, this.RouteId("invitation"), cancellationToken);
        return Results.NoContent();
    }
}
