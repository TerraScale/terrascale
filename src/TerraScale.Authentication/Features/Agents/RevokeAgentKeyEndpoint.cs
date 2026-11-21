using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class RevokeAgentKeyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Delete(Root + "/agents/{agent}/keys/{kid}");
        this.Describe("revoke_agent_key", "Revoke agent key", "Idempotent; the key stays listed with revoked_at. Agent identities. Requires the admin role or the corresponding project key scope.", "Agents", null, 204, null, conflict: true);
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await agents.RevokeKeyAsync(project, this.RouteId("agent"), this.RouteId("kid"), caller.Id, cancellationToken);
        return Results.NoContent();
    }
}
