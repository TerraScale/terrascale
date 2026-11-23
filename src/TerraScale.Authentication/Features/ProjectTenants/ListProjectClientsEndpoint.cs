using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ListProjectClientsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/clients");
        this.Describe("list_project_clients", "List OAuth clients", "Every client of the tenant in one page, without secrets. Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ClientPageBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return TypedResults.Json(new ClientPageBody([.. (await this.Tenants.ClientsAsync(projectId, cancellationToken)).Select(client => Map(client))], null), ProjectTenantJson.Default.ClientPageBody);
    }
}
