using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary><c>DELETE /v1/{project}/sessions/{session}</c>.</summary>
public sealed class RevokeProjectSessionEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Root + "/sessions/{session}");
        this.Document("revoke_project_session", "Revoke a session", "Needs the admin project role. Signs the session out; a revoked session answers 204 again.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await this.Tenants.RevokeSessionAsync(projectId, this.RouteId("session"), cancellationToken) ? Results.NoContent() : throw PlatformDirectory.NotFound();
    }
}
