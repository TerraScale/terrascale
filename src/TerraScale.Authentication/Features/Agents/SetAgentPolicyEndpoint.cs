using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class SetAgentPolicyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Put(Root + "/agent-policy");
        this.Describe("set_agent_policy", "Set agent policy", "Agent identities. Requires the admin role or the corresponding project key scope.", "Agents", typeof(AgentPolicyBody), 200, typeof(AgentPolicyBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(AgentJson.Default.AgentPolicyBody, cancellationToken);
        return TypedResults.Json(await agents.SetPolicyAsync(project, request.AllowedScopes, caller.Id, cancellationToken), AgentJson.Default.AgentPolicyBody);
    }
}
