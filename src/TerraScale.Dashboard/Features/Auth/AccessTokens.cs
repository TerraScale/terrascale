using Dapper;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AccessTokens(DashboardDb storage, DashboardSessions sessions, IAuthGateway auth, AccountService accounts,
    TimeProvider time, IAuthStepUpGateway stepUp) : IProfileTokens
{
    private const string Live = DashboardSql.AccessTokens.Select + " WHERE user_id = @UserId AND revoked_at IS NULL AND expires_at > @Now ORDER BY created_at DESC";
    private const string LiveCount = "SELECT count(*) FROM access_tokens WHERE user_id = @UserId AND revoked_at IS NULL AND expires_at > @Now";
    private const string Owned = DashboardSql.AccessTokens.Select + " WHERE id = @Id AND user_id = @UserId";
    private const string Revoke = "UPDATE access_tokens SET revoked_at = @RevokedAt WHERE id = @Id AND revoked_at IS NULL";
    private const string Forget = "DELETE FROM access_tokens WHERE user_id = @UserId; DELETE FROM sessions WHERE user_id = @UserId AND kind = @Credential";
    private const string BySecret = DashboardSql.AccessTokens.Select + " WHERE secret_hash = @Hash AND revoked_at IS NULL AND expires_at > @Now";
    private const string ClientTokens = "SELECT id FROM access_tokens WHERE user_id = @UserId AND mcp_client_id = @McpClientId AND revoked_at IS NULL";

    private const int MaxLiveTokens = 25;

    public async Task<IReadOnlyList<ProfileToken>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var connection = await storage.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AccessTokenRow>(Live, new { context.UserId, Now = now });
        return [.. rows.Select(Token)];
    }

    public Task<CreatedProfileToken> CreateAsync(AccountContext context, string name, DateTimeOffset expiresAt, SessionRecord fresh, StepUpIntent stepUp, CancellationToken cancellationToken)
    {
        return IssueAsync(context, name, expiresAt, fresh, stepUp, cancellationToken);
    }

    public static DateTimeOffset ExpiresAt(TimeProvider time, int expiresInDays) =>
        DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().AddDays(expiresInDays).ToUnixTimeSeconds());

    private async Task<CreatedProfileToken> IssueAsync(AccountContext context, string name, DateTimeOffset expiresAt, SessionRecord fresh, StepUpIntent proof, CancellationToken cancellationToken)
    {
        name = ValidName(name);
        if (fresh.UserId != context.UserId)
        {
            await auth.RevokeAsync(fresh, cancellationToken);
            throw new AccountAccessException("Identity does not match this account.", 401);
        }
        string? credential = null;
        try
        {
            if (proof.UserId != context.UserId || proof.Purpose != "create_pat" || proof.Audience != "ts-dashboard" || proof.Challenge.ExpiresAt <= time.GetUtcNow())
            {
                throw StepUpFlow.Required();
            }

            await stepUp.ConsumeAsync(context.Session, proof.Challenge.Proof, "create_pat", "ts-dashboard", null, null, cancellationToken);
            credential = await sessions.CreateCredentialAsync(fresh, expiresAt, cancellationToken);
            return await StoreAsync(context.UserId, name, expiresAt, credential, null, cancellationToken);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (credential is not null)
                {
                    await sessions.DeleteCredentialAsync(credential, cleanup.Token);
                }
            }
            finally { await auth.RevokeAsync(fresh, cleanup.Token); }
            throw;
        }
    }

    public async Task<CreatedProfileToken> IssueGrantedAsync(string userId, string name, DateTimeOffset expiresAt, string credentialId, string mcpClientId, CancellationToken cancellationToken)
    {
        CreatedProfileToken? created = null;
        try
        {
            if (await McpRegistrations.ReadActiveAsync(storage, time, mcpClientId, cancellationToken) is not { FirstSignInCompletedAt: not null })
            {
                throw new AccountAccessException("The MCP client registration expired. Add the server again.", 401);
            }

            created = await StoreAsync(userId, name, expiresAt, credentialId, mcpClientId, cancellationToken);
            if (await McpRegistrations.TouchAsync(storage, time, mcpClientId, cancellationToken) is null)
            {
                throw new AccountAccessException("The MCP client registration expired. Add the server again.", 401);
            }

            return created;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (created is null)
            {
                await ForgetGrantAsync(credentialId, cleanup.Token);
            }
            else
            {
                await RevokeAsync(userId, created.Token.Id, cleanup.Token);
            }

            throw;
        }
    }

    public async Task ForgetGrantAsync(string credentialId, CancellationToken cancellationToken)
    {
        if (await sessions.ReadCredentialAsync(credentialId, cancellationToken) is { } grant)
        {
            await auth.RevokeAsync(grant, cancellationToken);
        }

        await sessions.DeleteCredentialAsync(credentialId, cancellationToken);
    }

    private async Task<CreatedProfileToken> StoreAsync(string userId, string name, DateTimeOffset expiresAt, string credential, string? mcpClientId, CancellationToken cancellationToken)
    {
        var secret = "tspat_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        var row = new AccessTokenRow
        {
            Id = AccountService.NewId(),
            UserId = userId,
            Name = name,
            LastFour = secret[^4..],
            SecretHash = Hash(secret),
            CredentialId = credential,
            McpClientId = mcpClientId,
            CreatedAt = now.UtcDateTime,
            ExpiresAt = expiresAt.UtcDateTime,
        };
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            if (await connection.ExecuteScalarAsync<long>(LiveCount, new { UserId = userId, Now = now.UtcDateTime }) >= MaxLiveTokens)
            {
                throw new AccountAccessException("You can have up to 25 live personal access tokens.", 409);
            }

            await connection.ExecuteAsync(DashboardSql.AccessTokens.Insert, row);
        }

        await AccountEnrollment.ThrowIfErasedAsync(storage, userId, cancellationToken);
        return new(Token(row), secret);
    }

    public Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        return RevokeAsync(context.UserId, id, cancellationToken);
    }

    private async Task RevokeAsync(string userId, string id, CancellationToken cancellationToken)
    {
        string credential;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            var row = await connection.QuerySingleOrDefaultAsync<AccessTokenRow>(Owned, new { Id = id, UserId = userId })
                ?? throw new AccountAccessException("Access token was not found.", 404);
            credential = row.CredentialId;
            if (row.RevokedAt is null)
            {
                await connection.ExecuteAsync(Revoke, new { Id = id, RevokedAt = time.GetUtcNow().UtcDateTime });
            }
        }

        if (await sessions.ReadCredentialAsync(credential, cancellationToken) is { } grant)
        {
            await auth.RevokeAsync(grant, cancellationToken);
        }

        await sessions.DeleteCredentialAsync(credential, cancellationToken);
    }

    public async Task ForgetAllAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(Forget, new { UserId = userId, SessionRow.Credential });
    }

    public async Task<AccountContext?> ResolveAsync(string secret, CancellationToken cancellationToken)
    {
        if (secret.Length != 49 || !secret.StartsWith("tspat_", StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(secret);
        var now = time.GetUtcNow().UtcDateTime;
        AccessTokenRow? token;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            token = await connection.QuerySingleOrDefaultAsync<AccessTokenRow>(BySecret, new { Hash = hash, Now = now });
        }

        if (token is null)
        {
            return null;
        }

        var grant = await sessions.ReadCredentialAsync(token.CredentialId, cancellationToken);
        if (grant is null || grant.UserId != token.UserId)
        {
            return null;
        }

        var context = await accounts.GetContextAsync(grant, cancellationToken);
        if (token.McpClientId is { } clientId && await McpRegistrations.TouchAsync(storage, time, clientId, cancellationToken) is null)
        {
            await RevokeClientAsync(token.UserId, clientId, cancellationToken);
            return null;
        }

        return context;
    }

    private async Task RevokeClientAsync(string userId, string mcpClientId, CancellationToken cancellationToken)
    {
        List<string> live;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            live = (await connection.QueryAsync<string>(ClientTokens, new { UserId = userId, McpClientId = mcpClientId })).AsList();
        }

        foreach (var id in live)
        {
            await RevokeAsync(userId, id, cancellationToken);
        }
    }

    public static string Validate(string name, int expiresInDays)
    {
        name = ValidName(name);
        if (expiresInDays is < 1 or > 365)
        {
            throw new AccountAccessException("Choose an expiration between 1 and 365 days.", 422);
        }

        return name;
    }

    private static string ValidName(string name)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 64 || name.Any(char.IsControl))
        {
            throw new AccountAccessException("Access token names use 1–64 printable characters.", 422);
        }

        return name;
    }

    private static string Hash(string secret)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    private static ProfileToken Token(AccessTokenRow row)
    {
        return new(row.Id, row.Name, row.LastFour, new DateTimeOffset(row.CreatedAt), new DateTimeOffset(row.ExpiresAt));
    }
}
