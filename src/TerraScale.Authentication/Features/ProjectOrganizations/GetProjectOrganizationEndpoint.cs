using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class GetProjectOrganizationEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(OrganizationRoute);
        this.Describe("get_project_organization", "Get an organization", "Project-scoped organizations, separate from platform organizations. Reads require auth:read; mutations require auth:write. Membership changes preserve an admin and enforce max_members. Organization deletion cascades memberships, invitations and domains.", "Project tenants", null, 200, typeof(ProjectOrganizationBody), paged: false, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return Results.Json(ProjectOrganizationDirectory.Body((await organizations.ReadAsync(project, this.Org, cancellationToken)).Value), ProjectOrganizationsJson.Default.ProjectOrganizationBody);
    }
}
