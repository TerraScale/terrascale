using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class CreateAgentEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Post(Root + "/users/{user}/agents");
        this.Describe("create_agent", "Create agent", "Agent identities. Requires the admin role or the corresponding project key scope.", "Agents", typeof(CreateAgentRequest), 201, typeof(AgentBody), conflict: true);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(AgentJson.Default.CreateAgentRequest, cancellationToken);
        return TypedResults.Json(await agents.CreateAsync(project, this.RouteId("user"), caller.Id, request, static _ => [], null, cancellationToken), AgentJson.Default.AgentBody, statusCode: 201);
    }
}
