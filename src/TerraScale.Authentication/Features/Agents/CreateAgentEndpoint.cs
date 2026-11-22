using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class CreateAgentEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        Post(Root + "/users/{user}/agents");
        this.Describe("create_agent", "Create agent", "Agent identities. Requires the admin role or the corresponding project key scope.", "Agents", typeof(CreateAgentRequest), 201, typeof(AgentBody), conflict: true);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<Answer> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(AgentJson.Default.CreateAgentRequest, cancellationToken);
        return await agents.CreateAsync(project, this.RouteId("user"), caller.Id, request, static _ => [], null, cancellationToken) switch
        {
            AgentBody agent => Results.Json(agent, AgentJson.Default.AgentBody, statusCode: 201),
            Validation error => new Failure(error),
            NotFound error => new Failure(error),
            QuotaExceeded error => new Failure(error),
            Conflict error => new Failure(error),
        };
    }
}
