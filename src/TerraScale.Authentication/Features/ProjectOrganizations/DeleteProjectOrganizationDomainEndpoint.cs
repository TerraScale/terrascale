using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class DeleteProjectOrganizationDomainEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Delete(OrganizationRoute + "/domains/{domain}");
        this.Describe("delete_project_organization_domain", "Delete an organization domain", "Project-scoped verified domains. Publish record_name with record_value as DNS TXT before verifying. A domain belongs to one organization per project.", "Project tenants", null, 204, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await organizations.ChangeDomainAsync(project, this.Org, (string)this.HttpContext.GetRouteValue("domain")!, true, cancellationToken);
        return Results.NoContent();
    }
}
