using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.AgentId;

public sealed class ListAgentIdClientsEndpoint(PlatformAuthentication authentication, AgentIdClients clients) : AgentIdClientEndpoint(authentication)
{
    public override void Configure()
    {
        Get(Root);
        this.Describe("list_agentid_clients", "List your Sign in as agent clients",
            "The Sign in as agent clients the signed-in TerraScale account registered, at most 10, without their secrets. previous_secret_expires_at is set while a rotated-out secret still works. A token an agent acts with is 403 owner_required.",
            "Sign in as agent", null, StatusCodes.Status200OK, typeof(AgentIdClientPageBody), scoped: false);
    }

    protected override async Task<IResult> HandleOwnerAsync(UserRecord owner, CancellationToken cancellationToken) =>
        TypedResults.Json(await clients.ListAsync(owner.Id, cancellationToken), AgentIdClientJson.Default.AgentIdClientPageBody);
}
