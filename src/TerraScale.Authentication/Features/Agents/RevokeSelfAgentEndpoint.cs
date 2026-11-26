using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public sealed class RevokeSelfAgentEndpoint(PlatformAuthentication authentication, AuthOptions options, Sessions sessions, AgentDirectory agents)
    : SelfAgentEndpoint(authentication, options, sessions)
{
    public override void Configure()
    {
        AuthenticateInHandler();
        Delete(Root + "/{agent}");
        this.DescribeSelf("revoke_self_agent", "Revoke self agent", StatusCodes.Status204NoContent, null);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await agents.RevokeAsync(this.RouteId("project"), this.RouteId("agent"), caller.Id, caller.Id, cancellationToken);
        return TypedResults.NoContent();
    }
}
