using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Agents;

public sealed class ValidateAgentOwnerToken(LiveAgents agents) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.GetClaim(LiveAgents.Claim) is not { } agentId)
        {
            return;
        }

        if (context.Principal.GetClaim(Claims.Subject) is not { } ownerId || await agents.ActiveAsync(agentId, ownerId, context.CancellationToken).ConfigureAwait(false) is null)
        {
            context.Reject(OidcErrors.InvalidToken, "The agent was revoked or its owner signed out.");
        }
    }
}

public sealed class LimitAgentOwnerToken(TimeProvider clock) : IOpenIddictServerHandler<GenerateTokenContext>
{
    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Principal?.GetClaim(LiveAgents.Claim) is null)
        {
            return ValueTask.CompletedTask;
        }

        if (context.Transaction.GetHttpRequest()?.HttpContext.Items[AgentExchange.SubjectExpiry] is not DateTimeOffset subject || subject <= clock.GetUtcNow())
        {
            context.Reject(OidcErrors.InvalidGrant, "The subject token expired before the owner token was issued.");
            return ValueTask.CompletedTask;
        }

        if (context.Principal.GetExpirationDate() is not { } expiry || subject < expiry)
        {
            context.Principal.SetExpirationDate(subject);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class RefuseUnfencedAgentToken : IOpenIddictServerHandler<GenerateTokenContext>
{
    public ValueTask HandleAsync(GenerateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Transaction.GetHttpRequest()?.HttpContext.Items[AgentExchange.FenceBroken] is true)
        {
            context.Reject(OidcErrors.InvalidGrant, "The subject token is not a live delegated agent token of the TerraScale platform.");
        }

        return ValueTask.CompletedTask;
    }
}
