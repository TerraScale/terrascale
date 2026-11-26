using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public sealed class ListSelfAgentsEndpoint(PlatformAuthentication authentication, AuthOptions options, Sessions sessions, AgentDirectory agents)
    : SelfAgentEndpoint(authentication, options, sessions)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Get(Root);
        this.DescribeSelf("list_self_agents", "List self agents", StatusCodes.Status200OK, typeof(AgentPageBody));
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging(50);
        return TypedResults.Json(await agents.ListAsync(this.RouteId("project"), caller.Id, limit, after, cancellationToken), AgentJson.Default.AgentPageBody);
    }
}
