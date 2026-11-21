using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Agents;

public sealed class ShareAccountAgentOwnerEmailEndpoint(PlatformAuthentication authentication, AgentDirectory agents) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Patch("/v1/terrascale/me/agents/{agent}");
        this.Describe("share_account_agent_owner_email", "Share your email with apps your agent signs in to",
            "Sets share_owner_email (default false) on an agent the signed-in TerraScale account owns. While it is true, a registered Sign in as agent client that was granted the owner_email scope receives owner_email and owner_email_verified for sign-ins of this agent; while it is false they are omitted. UserInfo reads the flag on every call; ID tokens already issued keep what they carry until they expire (5 minutes). Another account's agent is 404; a token an agent acts with is 403 owner_required.",
            "Agents", typeof(AgentSharingRequest), StatusCodes.Status200OK, typeof(AgentBody), conflict: true);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        return AgentIdClients.Owner(caller) switch
        {
            UserRecord owner => await this.ShareAsync(owner, cancellationToken),
            Forbidden forbidden => ErrorResults.ToResult(forbidden),
        };
    }

    private async Task<IResult> ShareAsync(UserRecord owner, CancellationToken cancellationToken)
    {
        var agentId = this.RouteId("agent");
        var request = await this.BodyAsync(AgentJson.Default.AgentSharingRequest, cancellationToken);
        if (request.ShareOwnerEmail is not { } share)
        {
            return ErrorResults.ToResult(new Validation("share_owner_email must be true or false."));
        }

        return await agents.ShareOwnerEmailAsync(owner.InstanceId, agentId, owner.Id, share, cancellationToken) switch
        {
            AgentBody agent => Results.Json(agent, AgentJson.Default.AgentBody),
            NotFound notFound => ErrorResults.ToResult(notFound),
        };
    }
}
