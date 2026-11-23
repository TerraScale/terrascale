using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public abstract class AgentIdClientEndpoint(PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    protected const string Root = "/v1/terrascale/agentid-clients";

    protected sealed override Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        AgentIdClients.Owner(caller) switch
        {
            UserRecord owner => this.HandleOwnerAsync(owner, cancellationToken),
            AuthForbidden forbidden => Task.FromResult(ErrorResults.ToResult(forbidden)),
        };

    protected abstract Task<IResult> HandleOwnerAsync(UserRecord owner, CancellationToken cancellationToken);
}
