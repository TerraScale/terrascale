using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public sealed class RevokeAccountAgentEndpoint(PlatformAuthentication authentication, AgentDirectory agents) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Delete("/v1/terrascale/me/agents/{agent}");
        this.Describe("revoke_account_agent", "Revoke your agent",
            "Revokes an agent the signed-in TerraScale account owns, and every key of it: its next token, exchange or platform call is refused. Another account's agent is 404; revoking again is 204.",
            "Agents", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await agents.RevokeAsync(caller.InstanceId, this.RouteId("agent"), caller.Id, caller.Id, cancellationToken);
        return TypedResults.NoContent();
    }
}
