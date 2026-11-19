using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed record McpAgent(string AgentId, string OwnerId, bool Write);

public sealed class McpAgents(TsAuthGateway auth, AccountService accounts, AccountStore store, IMemoryCache owners, TimeProvider time)
{
    internal const string ContextKey = "TerraScale.Dashboard/mcp-agent";
    internal const string Refusal = "Agents can't do this. Ask the account owner to do it in the TerraScale dashboard.";
    internal const string ReadOnlyRefusal = "This agent token has only mcp:read. Ask for a token with mcp:write to make changes.";
    private static readonly TimeSpan ReuseMargin = TimeSpan.FromSeconds(30);

    public static bool IsAgentToken(string bearer)
    {
        return bearer.StartsWith("eyJ", StringComparison.Ordinal);
    }

    public static string? Refuse(McpAgent agent, McpAccess? access)
    {
        return access switch
        {
            McpAccess.Read => null,
            McpAccess.Write => agent.Write ? null : ReadOnlyRefusal,
            McpAccess.Destructive or null => Refusal,
            _ => throw new UnreachableException(),
        };
    }

    public async Task<(McpAgent Agent, AccountContext Context)?> SignInAsync(string token, CancellationToken cancellationToken)
    {
        if (await auth.VerifyAgentTokenAsync(token, cancellationToken) is not { } delegation)
        {
            return null;
        }

        var key = (nameof(McpAgents), delegation.AgentId, delegation.TokenId);
        if (!owners.TryGetValue(key, out AgentOwnerToken? owner) || owner is null)
        {
            owner = await auth.ExchangeAgentTokenAsync(token, delegation, cancellationToken);
            if (owner is null)
            {
                return null;
            }

            if (owner.ExpiresAt - ReuseMargin > time.GetUtcNow())
            {
                owners.Set(key, owner, owner.ExpiresAt - ReuseMargin);
            }
        }

        if (await store.FindAsync(delegation.OwnerId, cancellationToken) is not { Erased: false } stored)
        {
            return null;
        }

        var session = new SessionRecord(delegation.OwnerId, stored.Account.Name, stored.Account.Email, owner.AccessToken, null, owner.ExpiresAt);
        try
        {
            return (new(delegation.AgentId, delegation.OwnerId, delegation.Write), await accounts.GetContextAsync(session, cancellationToken));
        }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status401Unauthorized)
        {
            owners.Remove(key);
            return null;
        }
    }
}
