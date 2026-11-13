using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed class AddProjectOrganizationDomainEndpoint(ProjectOrganizationDirectory organizations, TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectOrganizationEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(OrganizationRoute + "/domains");
        this.Describe("add_project_organization_domain", "Add an organization domain", "Project-scoped verified domains. Publish record_name with record_value as DNS TXT before verifying. A domain belongs to one organization per project.", "Project tenants", typeof(ProjectDomainRequest), 201, typeof(ProjectOrganizationDomainBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.OrganizationBodyAsync(ProjectOrganizationsJson.Default.ProjectDomainRequest, cancellationToken);
        return Results.Json(await organizations.AddDomainAsync(project, this.Org, request.Domain, cancellationToken), ProjectOrganizationsJson.Default.ProjectOrganizationDomainBody, statusCode: 201);
    }
}
