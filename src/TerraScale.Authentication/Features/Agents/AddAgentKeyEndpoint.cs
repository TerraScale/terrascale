using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class AddAgentKeyEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Post(Root + "/agents/{agent}/keys");
        this.Describe("add_agent_key", "Add agent key", "An agent has at most two active keys. Agent identities. Requires the admin role or the corresponding project key scope.", "Agents", typeof(AddAgentKeyRequest), 201, typeof(AgentKeyBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(AgentJson.Default.AddAgentKeyRequest, cancellationToken);
        return TypedResults.Json(await agents.AddKeyAsync(project, this.RouteId("agent"), caller.Id, request, cancellationToken), AgentJson.Default.AgentKeyBody, statusCode: 201);
    }
}
