using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ListProjectClientsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/clients");
        this.Describe("list_project_clients", "List OAuth clients", "The tenant's clients without secrets; limit is 1 to 100 (default 100) and after the previous page's next. Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ClientPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(100);
        var page = await this.Tenants.ClientsAsync(projectId, limit, after, cancellationToken);
        return TypedResults.Json(new ClientPageBody([.. page.Items.Select(client => Map(client))], page.Next), ProjectTenantJson.Default.ClientPageBody);
    }
}
