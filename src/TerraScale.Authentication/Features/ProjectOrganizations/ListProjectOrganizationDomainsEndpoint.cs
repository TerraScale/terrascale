using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class ListProjectOrganizationDomainsEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(OrganizationRoute + "/domains");
        this.Describe("list_project_organization_domains", "List organization domains", "Project-scoped verified domains. Publish record_name with record_value as DNS TXT before verifying. A domain belongs to one organization per project.", "Project tenants", null, 200, typeof(ProjectOrganizationDomainPage), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return Results.Json(await organizations.DomainsAsync(project, this.Org, cancellationToken), ProjectOrganizationsJson.Default.ProjectOrganizationDomainPage);
    }
}
