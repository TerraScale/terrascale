using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed record McpAgent(string AgentId, string OwnerId, bool Write);

public sealed class McpAgents(TsAuthGateway auth, AccountService accounts, AccountStore store, TimeProvider time)
{
    internal const string Refusal = "Agents can't do this. Ask the account owner to do it in the TerraScale dashboard.";
    internal const string ReadOnlyRefusal = "This agent token has only mcp:read. Ask for a token with mcp:write to make changes.";
    private const int Capacity = 10_000;
    private static readonly TimeSpan Reuse = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Exchanged> exchanges = new(StringComparer.Ordinal);

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

    public async Task<McpAgentSignIn> SignInAsync(string token, CancellationToken cancellationToken)
    {
        var key = Key(token);
        var now = time.GetUtcNow();
        if (Cached(key, now) is { } cached)
        {
            return await ContextAsync(key, cached, cancellationToken);
        }

        var verification = await auth.VerifyAgentTokenAsync(token, cancellationToken);
        return verification switch
        {
            AgentDelegation delegation => await ExchangeAsync(key, token, delegation, now, cancellationToken),
            AgentTokenRejected => Forget(key),
            AgentTokenUnverifiable => new McpBearerUnverified(),
        };
    }

    private async Task<McpAgentSignIn> ExchangeAsync(string key, string token, AgentDelegation delegation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await auth.ExchangeAgentTokenAsync(token, delegation, cancellationToken) is not { } owner)
        {
            return Forget(key);
        }

        var exchanged = new Exchanged(delegation, owner, Min(now + Reuse, Min(owner.ExpiresAt, delegation.ExpiresAt) - ExpirySkew));
        Remember(key, exchanged, now);
        return await ContextAsync(key, exchanged, cancellationToken);
    }

    private async Task<McpAgentSignIn> ContextAsync(string key, Exchanged exchanged, CancellationToken cancellationToken)
    {
        var (delegation, owner) = (exchanged.Delegation, exchanged.Owner);
        if (await store.FindAsync(delegation.OwnerId, cancellationToken) is not { Erased: false } stored)
        {
            return Forget(key);
        }

        var session = new SessionRecord(delegation.OwnerId, stored.Account.Name, stored.Account.Email, owner.AccessToken, null, owner.ExpiresAt)
        {
            Agent = new(delegation.AgentId, delegation.OwnerId, delegation.Write && owner.Write),
        };
        try
        {
            return await accounts.GetContextAsync(session, cancellationToken);
        }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status401Unauthorized)
        {
            return Forget(key);
        }
    }

    private Exchanged? Cached(string key, DateTimeOffset now)
    {
        lock (gate)
        {
            return exchanges.TryGetValue(key, out var cached) && cached.Until > now ? cached : null;
        }
    }

    private void Remember(string key, Exchanged exchanged, DateTimeOffset now)
    {
        if (exchanged.Until <= now)
        {
            return;
        }

        lock (gate)
        {
            if (!exchanges.ContainsKey(key) && exchanges.Count >= Capacity)
            {
                foreach (var (stale, entry) in exchanges)
                {
                    if (entry.Until <= now)
                    {
                        exchanges.Remove(stale);
                    }
                }

                if (exchanges.Count >= Capacity)
                {
                    exchanges.Clear();
                }
            }

            exchanges[key] = exchanged;
        }
    }

    private McpBearerRejected Forget(string key)
    {
        lock (gate)
        {
            exchanges.Remove(key);
        }

        return new McpBearerRejected();
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record Exchanged(AgentDelegation Delegation, AgentOwnerToken Owner, DateTimeOffset Until);
}
