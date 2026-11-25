using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AgentDelegation(string AgentId, string OwnerId, string TokenId, bool Write, DateTimeOffset ExpiresAt);
public sealed record AgentOwnerToken(string AccessToken, DateTimeOffset ExpiresAt, bool Write);
public sealed record AgentTokenRejected;
public sealed record AgentTokenUnverifiable;
public readonly union AgentVerification(AgentDelegation, AgentTokenRejected, AgentTokenUnverifiable);

public sealed partial class TsAuthGateway
{
    public async Task<AgentVerification> VerifyAgentTokenAsync(string agentToken, CancellationToken cancellationToken)
    {
        var issuer = PlatformIssuer;
        if (!AuthJwtClaims.WellFormed(agentToken) || AuthJwtClaims.UnverifiedIssuer(agentToken) != issuer || AuthJwtClaims.KeyId(agentToken) is not { } kid)
        {
            return new AgentTokenRejected();
        }

        var client = Client();
        var (keys, fetched) = await TrackedKeysAsync(client, false, cancellationToken);
        if (!keys.Keys.Any(key => key.KeyId == kid))
        {
            if (fetched)
            {
                return new AgentTokenRejected();
            }

            var (refreshed, refetched) = await TrackedKeysAsync(client, true, cancellationToken);
            if (!refreshed.Keys.Any(key => key.KeyId == kid))
            {
                return refetched ? new AgentTokenRejected() : new AgentTokenUnverifiable();
            }

            keys = refreshed;
        }

        JsonElement claims;
        try { claims = AuthJwtClaims.Read(agentToken, issuer, null, keys, time.GetUtcNow(), "at+jwt", 8192); }
        catch (SignInRejectedException) { return new AgentTokenRejected(); }
        if (Text(claims, "token_use") != "agent" || Text(claims, "actor_type") != "agent" || claims.TryGetProperty("owner_sub", out _) ||
            Text(claims, "agent_id") is not { } agent || !TerraScaleIds.IsValid(agent) ||
            !claims.TryGetProperty("act", out var actor) || actor.ValueKind != JsonValueKind.Object || Text(actor, "sub") != agent ||
            Text(claims, "sub") is not { } owner || !TerraScaleIds.IsValid(owner) || owner == agent ||
            Text(claims, "jti") is not { Length: > 0 and <= 128 } tokenId || Text(claims, "scope") is not { } scope)
        {
            return new AgentTokenRejected();
        }

        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());
        return scopes.Contains("mcp:write") ? new AgentDelegation(agent, owner, tokenId, true, expiresAt)
            : scopes.Contains("mcp:read") ? new AgentDelegation(agent, owner, tokenId, false, expiresAt)
            : new AgentTokenRejected();
    }

    public async Task<AgentOwnerToken?> ExchangeAgentTokenAsync(string agentToken, AgentDelegation agent, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var client = Client();
        TsAuthTokenResponse tokens;
        try { tokens = await client.Oidc.ExchangeAgentTokenAsync(credentials.AuthClientId, credentials.AuthClientSecret, agentToken, cancellationToken); }
        catch (TsAuthException error) when (error.ErrorCode == "invalid_grant") { return null; }
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || !string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("ts-auth returned an invalid agent exchange.");
        }

        var issuer = TrustedIssuer((await MetadataAsync(client, cancellationToken)).Issuer);
        JsonElement claims;
        try { claims = AuthJwtClaims.Read(tokens.AccessToken, issuer, issuer, await SigningKeysAsync(client, tokens.AccessToken, cancellationToken), time.GetUtcNow(), "at+jwt"); }
        catch (SignInRejectedException error) { throw new IOException("ts-auth returned an invalid agent exchange.", error); }
        if (Text(claims, "sub") != agent.OwnerId || Text(claims, "agent_id") != agent.AgentId ||
            !claims.TryGetProperty("act", out var actor) || actor.ValueKind != JsonValueKind.Object || Text(actor, "sub") != agent.AgentId)
        {
            throw new IOException("ts-auth returned an owner token for another agent or owner.");
        }

        if (claims.TryGetProperty("ts_auth_mfa", out _) || claims.TryGetProperty("platform_role", out _) || claims.TryGetProperty("scope", out _) ||
            AgentScopes(claims) is not { } granted || granted.Except(["mcp:read", "mcp:write"], StringComparer.Ordinal).Any() ||
            granted.Contains("mcp:write") && !agent.Write || !granted.Contains("mcp:read") && !granted.Contains("mcp:write"))
        {
            throw new IOException("ts-auth returned an owner token beyond the agent's scope.");
        }

        return new(tokens.AccessToken, DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()), granted.Contains("mcp:write"));
    }

    private static string[]? AgentScopes(JsonElement claims) =>
        Text(claims, "agent_scope") is { } scope ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries) : null;

    private static string? Text(JsonElement claims, string name)
    {
        return claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
