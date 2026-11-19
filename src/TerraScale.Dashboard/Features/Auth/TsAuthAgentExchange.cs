using System.Text.Json;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AgentDelegation(string AgentId, string OwnerId, string TokenId, bool Write);
public sealed record AgentOwnerToken(string AccessToken, DateTimeOffset ExpiresAt);

public sealed partial class TsAuthGateway
{
    public async Task<AgentDelegation?> VerifyAgentTokenAsync(string agentToken, CancellationToken cancellationToken)
    {
        var client = Client();
        var issuer = TrustedIssuer((await MetadataAsync(client, cancellationToken)).Issuer);
        JsonElement claims;
        try { claims = AuthJwtClaims.Read(agentToken, issuer, null, await SigningKeysAsync(client, cancellationToken), time.GetUtcNow(), "at+jwt", 8192); }
        catch (SignInRejectedException) { return null; }
        if (Text(claims, "token_use") != "agent" || Text(claims, "actor_type") != "agent" || claims.TryGetProperty("owner_sub", out _) ||
            Text(claims, "agent_id") is not { } agent || !TerraScaleIds.IsValid(agent) ||
            !claims.TryGetProperty("act", out var actor) || actor.ValueKind != JsonValueKind.Object || Text(actor, "sub") != agent ||
            Text(claims, "sub") is not { } owner || !TerraScaleIds.IsValid(owner) || owner == agent ||
            Text(claims, "jti") is not { Length: > 0 and <= 128 } tokenId || Text(claims, "scope") is not { } scope)
        {
            return null;
        }

        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return scopes.Contains("mcp:write") ? new(agent, owner, tokenId, true)
            : scopes.Contains("mcp:read") ? new(agent, owner, tokenId, false)
            : null;
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
        try { claims = AuthJwtClaims.Read(tokens.AccessToken, issuer, issuer, await SigningKeysAsync(client, cancellationToken), time.GetUtcNow(), "at+jwt"); }
        catch (SignInRejectedException error) { throw new IOException("ts-auth returned an invalid agent exchange.", error); }
        if (Text(claims, "sub") != agent.OwnerId || Text(claims, "agent_id") != agent.AgentId ||
            !claims.TryGetProperty("act", out var actor) || actor.ValueKind != JsonValueKind.Object || Text(actor, "sub") != agent.AgentId)
        {
            throw new IOException("ts-auth returned an owner token for another agent or owner.");
        }

        return new(tokens.AccessToken, DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()));
    }

    private static string? Text(JsonElement claims, string name)
    {
        return claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
