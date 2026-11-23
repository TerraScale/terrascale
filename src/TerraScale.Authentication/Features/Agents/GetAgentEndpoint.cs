using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class GetAgentEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Get(Root + "/agents/{agent}");
        this.Describe("get_agent", "Get agent", "Agent identities. Requires the viewer role or the corresponding project key scope.", "Agents", null, 200, typeof(AgentBody));
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        return await agents.GetAsync(project, this.RouteId("agent"), cancellationToken) switch
        {
            AgentBody agent => TypedResults.Json(agent, AgentJson.Default.AgentBody),
            NotFound notFound => ErrorResults.ToResult(notFound),
        };
    }
}
