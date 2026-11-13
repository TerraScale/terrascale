using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class CreateProjectOrganizationInvitationEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(OrganizationRoute + "/invitations");
        this.Describe("create_project_organization_invitation", "Invite an organization member", "Invitation email names the organization and branded app; optional inviter_name is 1 to 100 plain-text characters without controls, bidi formatting or markup. The caller email and key name are never used. Project-scoped organizations, separate from platform organizations. Reads require auth:read; mutations require auth:write. Membership changes preserve an admin and enforce max_members. Organization deletion cascades memberships, invitations and domains.", "Project tenants", typeof(ProjectInvitationRequest), 201, typeof(ProjectOrganizationInvitationBody), paged: false, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.OrganizationBodyAsync(ProjectOrganizationsJson.Default.ProjectInvitationRequest, cancellationToken);
        return Results.Json(await organizations.InviteAsync(project, this.Org, request, cancellationToken), ProjectOrganizationsJson.Default.ProjectOrganizationInvitationBody, statusCode: 201);
    }
}
