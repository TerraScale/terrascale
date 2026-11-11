using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AccessTokens(IDbContextFactory<DashboardDbContext> storage, DashboardSessions sessions, IAuthGateway auth, AccountService accounts,
    TimeProvider time, IAuthStepUpGateway stepUp) : IProfileTokens
{
    private const int MaxLiveTokens = 25;

    public async Task<IReadOnlyList<ProfileToken>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var rows = await db.AccessTokens.AsNoTracking()
            .Where(row => row.UserId == context.UserId && row.RevokedAt == null && row.ExpiresAt > now)
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(Token)];
    }

    public Task<CreatedProfileToken> CreateAsync(AccountContext context, string name, int expiresInDays, SessionRecord fresh, StepUpIntent stepUp, CancellationToken cancellationToken)
    {
        return IssueAsync(context, name, expiresInDays, fresh, stepUp, cancellationToken);
    }

    private async Task<CreatedProfileToken> IssueAsync(AccountContext context, string name, int expiresInDays, SessionRecord fresh, StepUpIntent proof, CancellationToken cancellationToken)
    {
        name = Validate(name, expiresInDays);
        if (fresh.UserId != context.UserId)
        {
            await auth.RevokeAsync(fresh, cancellationToken);
            throw new AccountAccessException("Identity does not match this account.", 401);
        }
        var expiresAt = time.GetUtcNow().AddDays(expiresInDays);
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
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            var at = now.UtcDateTime;
            if (await db.AccessTokens.CountAsync(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > at, cancellationToken) >= MaxLiveTokens)
            {
                throw new AccountAccessException("You can have up to 25 live personal access tokens.", 409);
            }

            db.AccessTokens.Add(row);
            await db.SaveChangesAsync(cancellationToken);
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
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            var row = await db.AccessTokens.SingleOrDefaultAsync(token => token.Id == id && token.UserId == userId, cancellationToken)
                ?? throw new AccountAccessException("Access token was not found.", 404);
            credential = row.CredentialId;
            if (row.RevokedAt is null)
            {
                row.RevokedAt = time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
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
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await db.AccessTokens.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.Sessions.Where(row => row.UserId == userId && row.Kind == SessionRow.Credential).ExecuteDeleteAsync(cancellationToken);
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
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            token = await db.AccessTokens.AsNoTracking().SingleOrDefaultAsync(row => row.SecretHash == hash && row.RevokedAt == null && row.ExpiresAt > now, cancellationToken);
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
            return null;
        }

        return context;
    }

    public static string Validate(string name, int expiresInDays)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 64 || name.Any(char.IsControl))
        {
            throw new AccountAccessException("Access token names use 1–64 printable characters.", 422);
        }

        if (expiresInDays is < 1 or > 365)
        {
            throw new AccountAccessException("Choose an expiration between 1 and 365 days.", 422);
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
