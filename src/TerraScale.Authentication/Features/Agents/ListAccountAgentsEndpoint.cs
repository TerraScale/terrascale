using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public sealed class ListAccountAgentsEndpoint(PlatformAuthentication authentication, AgentDirectory agents) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Get("/v1/terrascale/account/agents");
        this.Describe("list_account_agents", "List your agents",
            "The agents the signed-in TerraScale account owns, enrolled through /v1/terrascale/agent-enrollments, with their scopes (mcp:read, mcp:write), keys and last_used_at.",
            "Agents", null, StatusCodes.Status200OK, typeof(AgentPageBody), paged: true, roleChecked: false, scoped: false);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging(50);
        return Results.Json(await agents.ListAsync(caller.InstanceId, caller.Id, limit, after, cancellationToken), AgentJson.Default.AgentPageBody);
    }
}
