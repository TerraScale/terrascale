using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>DELETE /v1/{project}/clients/{client_id}</c>.</summary>
public sealed class DeleteProjectClientEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Root + "/clients/{client_id}");
        this.Document("delete_project_client", "Delete an OAuth client", "Needs the admin project role.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await this.Tenants.DeleteClientAsync(projectId, this.RouteId("client_id"), cancellationToken) ? Results.NoContent() : throw PlatformDirectory.NotFound();
    }
}
