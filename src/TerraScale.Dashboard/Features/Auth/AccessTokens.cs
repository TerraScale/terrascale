using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AccessTokens(IDashboardState state, DashboardSessions sessions, IAuthGateway auth, AccountService accounts, TimeProvider time, IAuthStepUpGateway stepUp) : IProfileTokens
{
    public const string Collection = "dashboard_access_tokens";
    public const string IndexCollection = "dashboard_access_token_hashes";

    public async Task<IReadOnlyList<ProfileToken>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var set = await ReadAsync(context.UserId, cancellationToken);
        return set.Tokens.Where(token => token.RevokedAt is null && token.Token.ExpiresAt > time.GetUtcNow()).OrderByDescending(token => token.Token.CreatedAt).Select(token => token.Token).ToArray();
    }

    // The fresh grant comes from its own ts-auth authorization round trip, so the
    // token's rotating family is never copied from the browser session.
    public Task<CreatedProfileToken> CreateAsync(AccountContext context, string name, int expiresInDays, SessionRecord fresh, StepUpIntent proof, CancellationToken cancellationToken) =>
        IssueAsync(context, name, expiresInDays, fresh, proof, cancellationToken);

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
            if (proof.UserId != context.UserId || proof.Purpose != "create_pat" || proof.Audience != "ts-dashboard" || proof.Challenge.ExpiresAt <= time.GetUtcNow()) throw StepUpFlow.Required();
            await stepUp.ConsumeAsync(context.Session, proof.Challenge.Proof, "create_pat", "ts-dashboard", null, cancellationToken);
            credential = await sessions.CreateCredentialAsync(fresh, expiresAt, cancellationToken);
            return await StoreAsync(context.UserId, name, expiresAt, credential, null, cancellationToken);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { if (credential is not null) await sessions.DeleteCredentialAsync(credential, cleanup.Token); }
            finally { await auth.RevokeAsync(fresh, cleanup.Token); }
            throw;
        }
    }

    // An MCP OAuth client's token: its fresh grant was stored as a credential when
    // the client's own ts-auth authorization round trip finished.
    public async Task<CreatedProfileToken> IssueGrantedAsync(string userId, string name, DateTimeOffset expiresAt, string credentialId, string mcpClientId, CancellationToken cancellationToken)
    {
        CreatedProfileToken? created = null;
        try
        {
            if (await McpRegistrations.ReadActiveAsync(state, time, mcpClientId, cancellationToken) is not { FirstSignInCompletedAt: not null })
                throw new AccountAccessException("The MCP client registration expired. Add the server again.", 401);
            created = await StoreAsync(userId, name, expiresAt, credentialId, mcpClientId, cancellationToken);
            // No cross-document atomicity claim: recheck after storage, before publishing the secret.
            if (await McpRegistrations.TouchAsync(state, time, mcpClientId, cancellationToken) is null)
                throw new AccountAccessException("The MCP client registration expired. Add the server again.", 401);
            return created;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (created is null) await ForgetGrantAsync(credentialId, cleanup.Token);
            else await RevokeAsync(userId, created.Token.Id, cleanup.Token);
            throw;
        }
    }

    public async Task ForgetGrantAsync(string credentialId, CancellationToken cancellationToken)
    {
        if (await sessions.ReadCredentialAsync(credentialId, cancellationToken) is { } grant) await auth.RevokeAsync(grant, cancellationToken);
        await sessions.DeleteCredentialAsync(credentialId, cancellationToken);
    }

    private async Task<CreatedProfileToken> StoreAsync(string userId, string name, DateTimeOffset expiresAt, string credential, string? mcpClientId, CancellationToken cancellationToken)
    {
        var secret = "tspat_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = new ProfileToken(AccountService.NewId(), name, secret[^4..], time.GetUtcNow(), expiresAt);
        var stored = new StoredToken(token, Hash(secret), credential, McpClientId: mcpClientId);
        // The index alone cannot authenticate: the account's CAS-protected
        // token set must still contain the token on every request.
        await AccountEnrollment.RememberAsync(state, userId, IndexCollection, stored.SecretHash, cancellationToken);
        await AccountEnrollment.RememberAsync(state, userId, Collection, AccountService.AccountId(userId), cancellationToken);
        await state.PutAsync(IndexCollection, stored.SecretHash, JsonSerializer.SerializeToElement(new TokenOwner(userId, token.Id)), TerraBasePrecondition.Absent, cancellationToken);
        await UpdateAsync(userId, set =>
        {
            if (set.Tokens.Count(item => item.RevokedAt is null && item.Token.ExpiresAt > time.GetUtcNow()) >= 25) throw new AccountAccessException("You can have up to 25 live personal access tokens.", 409);
            set.Tokens.Add(stored);
        }, cancellationToken);
        await AccountEnrollment.ThrowIfErasedAsync(state, userId, cancellationToken);
        return new(token, secret);
    }

    public Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken) =>
        RevokeAsync(context.UserId, id, cancellationToken);

    private async Task RevokeAsync(string userId, string id, CancellationToken cancellationToken)
    {
        StoredToken? removed = null;
        await UpdateAsync(userId, set =>
        {
            removed = set.Tokens.SingleOrDefault(item => item.Token.Id == id) ?? throw new AccountAccessException("Access token was not found.", 404);
            var position = set.Tokens.IndexOf(removed);
            // Deny requests immediately but retain the encrypted grant reference
            // until provider revocation and cleanup succeed, so retries can finish.
            set.Tokens[position] = removed with { RevokedAt = removed.RevokedAt ?? time.GetUtcNow() };
        }, cancellationToken);
        var token = removed!;
        var grant = await sessions.ReadCredentialAsync(token.CredentialId, cancellationToken);
        if (grant is not null) await auth.RevokeAsync(grant, cancellationToken);
        await sessions.DeleteCredentialAsync(token.CredentialId, cancellationToken);
        var index = await state.GetAsync(IndexCollection, token.SecretHash, cancellationToken);
        if (index is not null)
        {
            if (index.Id != token.SecretHash || AccountService.Read<TokenOwner>(index) != new TokenOwner(userId, token.Token.Id))
                throw new IOException("Access token index ownership changed.");
            await state.DeleteAsync(IndexCollection, token.SecretHash, index.Version, cancellationToken);
        }
        await UpdateAsync(userId, set => set.Tokens.RemoveAll(item => item.Token.Id == id), cancellationToken);
    }

    // Account deletion: ts-auth has already revoked every grant, so only the dashboard's records remain.
    public async Task ForgetAllAsync(string userId, CancellationToken cancellationToken)
    {
        var id = AccountService.AccountId(userId);
        var document = await state.GetAsync(Collection, id, cancellationToken);
        if (document is null) return;
        var set = await ReadAsync(userId, cancellationToken);
        foreach (var token in set.Tokens)
        {
            await sessions.DeleteCredentialAsync(token.CredentialId, userId, cancellationToken);
            if (await state.GetAsync(IndexCollection, token.SecretHash, cancellationToken) is { } index)
            {
                if (index.Id != token.SecretHash || AccountService.Read<TokenOwner>(index) != new TokenOwner(userId, token.Token.Id))
                    throw new IOException("Access token index ownership changed.");
                await state.DeleteAsync(IndexCollection, token.SecretHash, index.Version, cancellationToken);
            }
        }
        await state.DeleteAsync(Collection, id, document.Version, cancellationToken);
    }

    public async Task<AccountContext?> ResolveAsync(string secret, CancellationToken cancellationToken)
    {
        if (secret.Length != 49 || !secret.StartsWith("tspat_", StringComparison.Ordinal)) return null;
        var hash = Hash(secret);
        var index = await state.GetAsync(IndexCollection, hash, cancellationToken);
        if (index is not null && index.Id != hash) throw new IOException("Access token index identity does not match.");
        var owner = index is null ? null : AccountService.Read<TokenOwner>(index);
        if (owner is null) return null;
        var set = await ReadAsync(owner.UserId, cancellationToken);
        var token = set.Tokens.SingleOrDefault(item => item.RevokedAt is null && item.Token.Id == owner.TokenId && item.SecretHash == hash && item.Token.ExpiresAt > time.GetUtcNow());
        if (token is null) return null;
        var grant = await sessions.ReadCredentialAsync(token.CredentialId, cancellationToken);
        if (grant is null)
        {
            // The ts-auth grant behind this token is gone for good; stop listing a token that can no longer sign in.
            await UpdateAsync(owner.UserId, set =>
            {
                var position = set.Tokens.FindIndex(item => item.Token.Id == owner.TokenId && item.RevokedAt is null);
                if (position >= 0) set.Tokens[position] = set.Tokens[position] with { RevokedAt = time.GetUtcNow() };
            }, cancellationToken);
            return null;
        }
        if (grant.UserId != owner.UserId) return null;
        var context = await accounts.GetContextAsync(grant, cancellationToken);
        // Issuer, credential subject, and account validity precede registration activity.
        if (token.McpClientId is { } clientId && await McpRegistrations.TouchAsync(state, time, clientId, cancellationToken) is null) return null;
        return context;
    }

    private async Task<TokenSet> ReadAsync(string userId, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(Collection, AccountService.AccountId(userId), cancellationToken);
        if (document is not null && document.Id != AccountService.AccountId(userId)) throw new IOException("Access token set identity does not match.");
        var set = document is null ? new TokenSet { UserId = userId } : AccountService.Read<TokenSet>(document);
        if (set.UserId != userId) throw new AccountAccessException("Access token ownership does not match.");
        return set;
    }

    private async Task UpdateAsync(string userId, Action<TokenSet> update, CancellationToken cancellationToken)
    {
        var id = AccountService.AccountId(userId);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var document = await state.GetAsync(Collection, id, cancellationToken);
            if (document is not null && document.Id != id) throw new IOException("Access token set identity does not match.");
            var set = document is null ? new TokenSet { UserId = userId } : AccountService.Read<TokenSet>(document);
            if (set.UserId != userId) throw new AccountAccessException("Access token ownership does not match.");
            update(set);
            try { await state.PutAsync(Collection, id, JsonSerializer.SerializeToElement(set), document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken); return; }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }

    public static string Validate(string name, int expiresInDays)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 64 || name.Any(char.IsControl)) throw new AccountAccessException("Access token names use 1–64 printable characters.", 422);
        if (expiresInDays is < 1 or > 365) throw new AccountAccessException("Choose an expiration between 1 and 365 days.", 422);
        return name;
    }

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public sealed class TokenSet
    {
        public string UserId { get; set; } = "";
        public List<StoredToken> Tokens { get; set; } = [];
    }
    public sealed record TokenOwner(string UserId, string TokenId);
    public sealed record StoredToken(ProfileToken Token, string SecretHash, string CredentialId, DateTimeOffset? RevokedAt = null, string? McpClientId = null);
}
