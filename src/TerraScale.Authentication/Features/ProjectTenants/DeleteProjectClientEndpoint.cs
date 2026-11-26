using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class DeleteProjectClientEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Delete(Root + "/clients/{client_id}");
        this.Describe("delete_project_client", "Delete an OAuth client", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await this.Tenants.DeleteClientAsync(projectId, this.RouteId("client_id"), cancellationToken) ? TypedResults.NoContent() : throw PlatformDirectory.NotFound();
    }
}
