using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class GetAgentPolicyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/agent-policy");
        this.Describe("get_agent_policy", "Get agent policy", "Agent identities. Requires the viewer role or the corresponding project key scope.", "Agents", null, 200, typeof(AgentPolicyBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return TypedResults.Json(await agents.PolicyAsync(project, cancellationToken), AgentJson.Default.AgentPolicyBody);
    }
}
