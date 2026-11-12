using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class UpdateProjectOrganizationEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Put(OrganizationRoute);
        this.Describe("update_project_organization", "Update an organization", "Project-scoped organizations, separate from platform organizations. Reads require auth:read; mutations require auth:write. Membership changes preserve an admin and enforce max_members. Organization deletion cascades memberships, invitations and domains.", "Project tenants", typeof(ProjectOrganizationRequest), 200, typeof(ProjectOrganizationBody), paged: false, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.OrganizationBodyAsync(ProjectOrganizationsJson.Default.ProjectOrganizationRequest, cancellationToken);
        return Results.Json(await organizations.UpdateAsync(project, this.Org, request, cancellationToken), ProjectOrganizationsJson.Default.ProjectOrganizationBody);
    }
}
