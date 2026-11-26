using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Agents;

public sealed class ListUserAgentsEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, AgentDirectory agents) : ProjectTenantEndpoint(tenants, authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Get(Root + "/users/{user}/agents");
        this.Describe("list_user_agents", "List user agents", "limit is 1 to 100 (default 50) and after the previous page's next. Agent identities. Requires the viewer role or the corresponding project key scope.", "Agents", null, 200, typeof(AgentPageBody), paged: true);
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Viewer, cancellationToken);
        var (limit, after) = this.Paging(50);
        var user = this.RouteId("user");
        _ = await this.Tenants.UserAsync(project, user, cancellationToken);
        return TypedResults.Json(await agents.ListAsync(project, user, limit, after, cancellationToken), AgentJson.Default.AgentPageBody);
    }
}
