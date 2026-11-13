using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class VerifyProjectOrganizationDomainEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(OrganizationRoute + "/domains/{domain}/verify");
        this.Describe("verify_project_organization_domain", "Verify an organization domain", "Project-scoped verified domains. Publish record_name with record_value as DNS TXT before verifying. A domain belongs to one organization per project. Resolver errors answer 503 dns_unavailable.", "Project tenants", null, 200, typeof(ProjectOrganizationDomainBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return Results.Json(await organizations.ChangeDomainAsync(project, this.Org, (string)this.HttpContext.GetRouteValue("domain")!, false, cancellationToken), ProjectOrganizationsJson.Default.ProjectOrganizationDomainBody);
    }
}
