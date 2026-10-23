using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>DELETE /v1/{project}/users/{user}</c>.</summary>
public sealed class DeleteProjectUserEndpoint(TenantDirectory tenants, Users users, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Root + "/users/{user}");
        this.Document("delete_project_user", "Delete an end user", "Needs the admin project role. Its email is released and its sessions and tokens stop working at once.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        _ = await this.Tenants.UserAsync(projectId, this.RouteId("user"), cancellationToken);
        await users.DeleteAsync(projectId, this.RouteId("user"), cancellationToken);
        return Results.NoContent();
    }
}
