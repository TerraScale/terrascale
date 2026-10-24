using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>GET /v1/{project}/clients</c>.</summary>
public sealed class ListProjectClientsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Root + "/clients");
        this.Document("list_project_clients", "List OAuth clients", "Every client of the tenant in one page, without secrets. Needs the viewer project role.", "Project tenants", null, StatusCodes.Status200OK, typeof(ClientPageBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return Results.Json(new ClientPageBody([.. (await this.Tenants.ClientsAsync(projectId, cancellationToken)).Select(client => Map(client))], null), ProjectTenantJson.Default.ClientPageBody);
    }
}
