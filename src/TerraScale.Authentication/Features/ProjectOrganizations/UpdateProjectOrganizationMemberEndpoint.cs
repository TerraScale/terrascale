using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class UpdateProjectOrganizationMemberEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Put(OrganizationRoute + "/memberships/{user}");
        this.Describe("update_project_organization_member", "Change an organization role", "Project-scoped organizations, separate from platform organizations. Reads require auth:read; mutations require auth:write. Membership changes preserve an admin and enforce max_members. Organization deletion cascades memberships, invitations and domains.", "Project tenants", typeof(ProjectMembershipRequest), 200, typeof(ProjectOrganizationMembership), paged: false, conflict: true);
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.OrganizationBodyAsync(ProjectOrganizationsJson.Default.ProjectMembershipRequest, cancellationToken);
        return await organizations.ChangeMemberAsync(project, this.Org, this.RouteId("user"), request.Role, false, cancellationToken) switch
        {
            ProjectOrganizationMembership changed => Results.Json(changed, ProjectOrganizationsJson.Default.ProjectOrganizationMembership),
            Done => Results.NoContent(),
            NotFound error => new Failure(error),
            Validation error => new Failure(error),
            Conflict error => new Failure(error)
        };

    }
}
