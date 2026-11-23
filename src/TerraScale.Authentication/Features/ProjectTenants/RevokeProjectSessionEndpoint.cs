using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class RevokeProjectSessionEndpoint(TenantDirectory tenants, PlatformAuthentication authentication) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Delete(Root + "/sessions/{session}");
        this.Describe("revoke_project_session", "Revoke a session", "Needs the admin project role. Signs the session out; a revoked session answers 204 again.", "Project tenants", null, StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var projectId = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        return await this.Tenants.RevokeSessionAsync(projectId, this.RouteId("session"), cancellationToken) ? TypedResults.NoContent() : throw PlatformDirectory.NotFound();
    }
}
