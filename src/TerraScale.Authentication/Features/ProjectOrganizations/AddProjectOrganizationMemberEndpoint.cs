using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class AddProjectOrganizationMemberEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(OrganizationRoute + "/memberships");
        this.Describe("add_project_organization_member", "Add an organization member", "Project-scoped organizations, separate from platform organizations. Reads require auth:read; mutations require auth:write. Membership changes preserve an admin and enforce max_members. Organization deletion cascades memberships, invitations and domains.", "Project tenants", typeof(ProjectMembershipRequest), 201, typeof(ProjectOrganizationMembership), paged: false, conflict: true);
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.OrganizationBodyAsync(ProjectOrganizationsJson.Default.ProjectMembershipRequest, cancellationToken);
        return await organizations.AddAsync(project, this.Org, request.UserId, request.Role, cancellationToken) switch
        {
            ProjectOrganizationMembership member => Results.Json(member, ProjectOrganizationsJson.Default.ProjectOrganizationMembership, statusCode: 201),
            NotFound error => ErrorResults.ToResult(error),
            Validation error => ErrorResults.ToResult(error),
            Conflict error => ErrorResults.ToResult(error)
        };

    }
}
