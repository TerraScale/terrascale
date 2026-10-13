using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

public sealed class AccessTokens(IDashboardState state, DashboardSessions sessions, IAuthGateway auth, AccountService accounts, TimeProvider time) : IProfileTokens
{
    public const string Collection = "dashboard_access_tokens";
    public const string IndexCollection = "dashboard_access_token_hashes";

    public async Task<IReadOnlyList<ProfileToken>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var set = await ReadAsync(context.UserId, cancellationToken);
        return set.Tokens.Where(token => token.RevokedAt is null && token.Token.ExpiresAt > time.GetUtcNow()).OrderByDescending(token => token.Token.CreatedAt).Select(token => token.Token).ToArray();
    }

    public async Task<CreatedProfileToken> CreateAsync(AccountContext context, string name, int expiresInDays, string password, string clientIp, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 64 || name.Any(char.IsControl)) throw new AccountAccessException("Access token names use 1–64 printable characters.", 422);
        if (expiresInDays is < 1 or > 365) throw new AccountAccessException("Choose an expiration between 1 and 365 days.", 422);
        if (string.IsNullOrEmpty(password)) throw new AccountAccessException("Enter your current password.", 422);
        SessionRecord fresh;
        try { fresh = await auth.PasswordAsync(context.Account.Email, password, clientIp, cancellationToken); }
        catch (SignInRejectedException) { throw new AccountAccessException("Current password is incorrect.", 401); }
        return await IssueAsync(context, name, expiresInDays, fresh, null, cancellationToken);
    }

    // CLI approval owns a separate password grant. Polling transfers that
    // encrypted credential into a PAT without copying a rotating token family.
    // The caller must consume its single-use grant before invoking this method.
    public async Task<CreatedProfileToken> CreateFromCredentialAsync(AccountContext context, string name, int expiresInDays, string credentialId, CancellationToken cancellationToken)
    {
        var grant = await sessions.ReadCredentialAsync(credentialId, cancellationToken)
            ?? throw new AccountAccessException("Authorization has expired.", 401);
        return await IssueAsync(context, name, expiresInDays, grant, credentialId, cancellationToken);
    }

    private async Task<CreatedProfileToken> IssueAsync(AccountContext context, string name, int expiresInDays, SessionRecord fresh, string? credential, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() is 0 or > 64 || name.Any(char.IsControl)) throw new AccountAccessException("Access token names use 1–64 printable characters.", 422);
        if (expiresInDays is < 1 or > 365) throw new AccountAccessException("Choose an expiration between 1 and 365 days.", 422);
        if (fresh.UserId != context.UserId)
        {
            await auth.RevokeAsync(fresh, cancellationToken);
            throw new AccountAccessException("Identity does not match this account.", 401);
        }
        var now = time.GetUtcNow();
        var secret = "tspat_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = fresh.SessionExpiresAt is { } credentialExpiry && credentialExpiry < now.AddDays(expiresInDays) ? credentialExpiry : now.AddDays(expiresInDays);
        var token = new ProfileToken(Guid.CreateVersion7().ToString(), name, secret[^4..], now, expiresAt);
        try
        {
            credential ??= await sessions.CreateCredentialAsync(fresh, token.ExpiresAt, cancellationToken);
            var stored = new StoredToken(token, Hash(secret), credential);
            // The index alone cannot authenticate: the account's CAS-protected
            // token set must still contain the token on every request.
            await state.PutAsync(IndexCollection, stored.SecretHash, JsonSerializer.SerializeToElement(new TokenOwner(context.UserId, token.Id)), TerraBasePrecondition.Absent, cancellationToken);
            await UpdateAsync(context.UserId, set =>
            {
                if (set.Tokens.Count(item => item.RevokedAt is null && item.Token.ExpiresAt > time.GetUtcNow()) >= 25) throw new AccountAccessException("You can have up to 25 live personal access tokens.", 409);
                set.Tokens.Add(stored);
            }, cancellationToken);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { if (credential is not null) await sessions.DeleteCredentialAsync(credential, cleanup.Token); }
            finally { await auth.RevokeAsync(fresh, cleanup.Token); }
            throw;
        }
        return new(token, secret);
    }

    public Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken) =>
        RevokeAsync(context.UserId, id, cancellationToken);

    // Operator jobs do not retain the device PAT after the command finishes.
    // Possession of the exact hashed secret permits only its own revocation,
    // including cleanup after its issuer grant became unavailable.
    public async Task RevokeSecretAsync(string secret, CancellationToken cancellationToken)
    {
        if (secret.Length != 49 || !secret.StartsWith("tspat_", StringComparison.Ordinal)) return;
        var hash = Hash(secret);
        var index = await state.GetAsync(IndexCollection, hash, cancellationToken);
        if (index is null) return;
        if (index.Id != hash) throw new IOException("Access token index identity does not match.");
        var owner = AccountService.Read<TokenOwner>(index);
        var set = await ReadAsync(owner.UserId, cancellationToken);
        var token = set.Tokens.SingleOrDefault(item => item.Token.Id == owner.TokenId && item.SecretHash == hash);
        if (token is not null) await RevokeAsync(owner.UserId, token.Token.Id, cancellationToken);
    }

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

    public Task<AccountContext?> ResolveAsync(string secret, CancellationToken cancellationToken) =>
        ResolveAsync(secret, cancellationToken, requireFreshPrivilege: false);

    // Operator commands must verify a role minted after their first credential
    // read. Ordinary PAT/MCP requests retain their existing refresh behavior.
    public Task<AccountContext?> ResolveForPrivilegeAsync(string secret, CancellationToken cancellationToken) =>
        ResolveAsync(secret, cancellationToken, requireFreshPrivilege: true);

    private async Task<AccountContext?> ResolveAsync(string secret, CancellationToken cancellationToken, bool requireFreshPrivilege)
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
        var grant = requireFreshPrivilege
            ? await sessions.ReadCredentialForPrivilegeAsync(token.CredentialId, cancellationToken)
            : await sessions.ReadCredentialAsync(token.CredentialId, cancellationToken);
        if (grant is null || grant.UserId != owner.UserId) return null;
        if (requireFreshPrivilege)
        {
            // A revocation that wins while the issuer is rotating the encrypted
            // family must deny this operator action before it reads its target.
            var current = await ReadAsync(owner.UserId, cancellationToken);
            if (!current.Tokens.Any(item => item.RevokedAt is null && item.Token.Id == token.Token.Id &&
                item.SecretHash == hash && item.CredentialId == token.CredentialId && item.Token.ExpiresAt > time.GetUtcNow())) return null;
            var latestIndex = await state.GetAsync(IndexCollection, hash, cancellationToken);
            if (latestIndex is null) return null;
            if (latestIndex.Id != hash || AccountService.Read<TokenOwner>(latestIndex) != owner)
                throw new IOException("Access token index ownership changed.");
        }
        return await accounts.GetContextAsync(grant, cancellationToken);
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

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public sealed class TokenSet
    {
        public string UserId { get; set; } = "";
        public List<StoredToken> Tokens { get; set; } = [];
    }
    public sealed record TokenOwner(string UserId, string TokenId);
    public sealed record StoredToken(ProfileToken Token, string SecretHash, string CredentialId, DateTimeOffset? RevokedAt = null);
}
