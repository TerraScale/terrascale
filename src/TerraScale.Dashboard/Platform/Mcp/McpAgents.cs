using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, Exchanged> exchanges = new(StringComparer.Ordinal);

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

    public async Task<AccountContext?> SignInAsync(string token, CancellationToken cancellationToken)
    {
        if (await ExchangeAsync(token, cancellationToken) is not { } exchanged)
        {
            return null;
        }

        var (delegation, owner) = (exchanged.Delegation, exchanged.Owner);
        if (await store.FindAsync(delegation.OwnerId, cancellationToken) is not { Erased: false } stored)
        {
            return null;
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
            exchanges.TryRemove(Key(token), out _);
            return null;
        }
    }

    private async Task<Exchanged?> ExchangeAsync(string token, CancellationToken cancellationToken)
    {
        var key = Key(token);
        var now = time.GetUtcNow();
        if (exchanges.TryGetValue(key, out var cached) && cached.Until > now)
        {
            return cached;
        }

        if (await auth.VerifyAgentTokenAsync(token, cancellationToken) is not { } delegation ||
            await auth.ExchangeAgentTokenAsync(token, delegation, cancellationToken) is not { } owner)
        {
            exchanges.TryRemove(key, out _);
            return null;
        }

        var until = Min(now + Reuse, Min(owner.ExpiresAt, delegation.ExpiresAt) - ExpirySkew);
        var exchanged = new Exchanged(delegation, owner, until);
        if (until > now)
        {
            if (exchanges.Count >= Capacity)
            {
                foreach (var (stale, entry) in exchanges)
                {
                    if (entry.Until <= now)
                    {
                        exchanges.TryRemove(stale, out _);
                    }
                }

                if (exchanges.Count >= Capacity)
                {
                    exchanges.Clear();
                }
            }

            exchanges[key] = exchanged;
        }

        return exchanged;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record Exchanged(AgentDelegation Delegation, AgentOwnerToken Owner, DateTimeOffset Until);
}
