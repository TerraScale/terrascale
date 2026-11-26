using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class RevokeAgentEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Delete(Root + "/agents/{agent}");
        this.Describe("revoke_agent", "Revoke agent", "Idempotent; the agent stays listed as revoked. Agent identities. Requires the admin role or the corresponding project key scope.", "Agents", null, 204, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        await agents.RevokeAsync(project, this.RouteId("agent"), null, caller.Id, cancellationToken);
        return TypedResults.NoContent();
    }
}
