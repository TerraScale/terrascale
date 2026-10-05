using System.Text.Json;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Security;
using TerraScale.Auth.Store;

namespace TerraScale.Auth.Features.Identity;

/// <summary>A newly issued session and the bearer token for its cookie. The token is never stored.</summary>
/// <param name="Token">The bearer token; return it to the browser once.</param>
/// <param name="Session">The stored session.</param>
public sealed record IssuedSession(string Token, SessionRecord Session)
{
    /// <summary>Omits the bearer token, so formatting or logging the record never discloses it.</summary>
    public override string ToString() => nameof(IssuedSession);
}

/// <summary>
/// Server-side browser sessions. A session is valid until its absolute expiry unless it is revoked or the user's
/// authentication epoch advances. Every check reads TerraBase, so a revocation is seen by every replica at once.
/// Callers authorize the instance before using these operations.
/// </summary>
public sealed class Sessions(AuthStore store, Users users, TimeProvider clock)
{
    /// <summary>Absolute session lifetime from authentication.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    // Issued tokens are 48 characters; anything far longer is never ours and is not hashed or looked up.
    private const int MaxTokenLength = 256;

    /// <summary>Opens a session for a user who has just authenticated.</summary>
    public Task<IssuedSession> CreateAsync(UserRecord user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return this.IssueAsync(user, clock.GetUtcNow() + Lifetime, cancellationToken);
    }

    /// <summary>The live session for <paramref name="token"/> and its user, or <see langword="null"/>.</summary>
    public async Task<(SessionRecord Session, UserRecord User)?> ResolveAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        await this.ReadLiveAsync(instanceId, token, cancellationToken).ConfigureAwait(false) is { } live ? (live.Session, live.User) : null;

    /// <summary>
    /// Replaces a live session with a new token after a privilege change. The old token stops working first, so
    /// concurrent rotations of one session produce exactly one successor; the successor keeps the old expiry.
    /// Returns <see langword="null"/> when the session is not live or another rotation or sign-out won.
    /// </summary>
    public async Task<IssuedSession?> RotateAsync(string instanceId, string? token, CancellationToken cancellationToken = default)
    {
        if (await this.ReadLiveAsync(instanceId, token, cancellationToken).ConfigureAwait(false) is not { } live
            || !await this.TryRevokeAsync(live.Session, live.Version, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var successor = await this.IssueAsync(live.User, live.Session.ExpiresAt, cancellationToken).ConfigureAwait(false);
        // A rotation that completes after the absolute expiry hands out nothing.
        return successor.Session.ExpiresAt > clock.GetUtcNow() ? successor : null;
    }

    /// <summary>Signs one session out. True once the session is revoked, including when it already was; false once pruned.</summary>
    public async Task<bool> RevokeAsync(string instanceId, string? token, CancellationToken cancellationToken = default)
    {
        if (token is not { Length: > 0 and <= MaxTokenLength })
        {
            return false;
        }

        // Stored sessions are only ever rewritten to revoke them, so a lost write means another revocation won.
        for (var round = 0; round < 2; round++)
        {
            var document = await store.GetAsync(AuthStore.SessionCollection, instanceId, Tokens.Sha256Hex(token), cancellationToken).ConfigureAwait(false);
            var session = document?.Document.Deserialize(IdentityJson.Default.SessionRecord);
            if (session is null || session.InstanceId != instanceId)
            {
                return false;
            }

            if (session.Revoked || await this.TryRevokeAsync(session, document!.Version, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Signs the user out everywhere by advancing their authentication epoch, which also voids outstanding passcodes.
    /// False when the user does not exist in the instance.
    /// </summary>
    public async Task<bool> RevokeAllAsync(string instanceId, string userId, CancellationToken cancellationToken = default)
    {
        long? initial = null;
        for (var round = 0; round < 8; round++)
        {
            if (await users.GetVersionedAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            initial ??= current.User.AuthEpoch;
            // Another writer already advanced the epoch past every session issued before this call.
            if (current.User.AuthEpoch > initial)
            {
                return true;
            }

            var advanced = JsonSerializer.SerializeToElement(current.User with { AuthEpoch = current.User.AuthEpoch + 1 }, IdentityJson.Default.UserRecord);
            if (await store.CompareAndPutAsync(AuthStore.UserCollection, instanceId, userId, advanced, current.Version, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>
    /// Deletes revoked and expired sessions of every instance in batches of <paramref name="batchSize"/>, each only at the
    /// version read. A dead session already resolves to nothing and never becomes live again, so deleting it changes no
    /// authorization outcome; it keeps storage proportional to live sessions. Returns how many this call deleted.
    /// </summary>
    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Expiries are written from UTC instants, so their ISO 8601 text orders chronologically.
        var now = JsonSerializer.Serialize(clock.GetUtcNow(), IdentityJson.Default.DateTimeOffset);
        using var filter = JsonDocument.Parse($$"""{"or":[{"eq":["/revoked",true]},{"lte":["/expiresAt",{{now}}]}]}""");
        var total = 0;
        while (true)
        {
            var batch = await store.QueryAsync(AuthStore.SessionCollection, filter.RootElement, batchSize, cancellationToken).ConfigureAwait(false);
            var deleted = 0;
            foreach (var document in batch)
            {
                if (document.Document.Deserialize(IdentityJson.Default.SessionRecord) is { } session
                    && await store.CompareAndDeleteAsync(AuthStore.SessionCollection, session.InstanceId, session.TokenHash, document.Version, cancellationToken).ConfigureAwait(false))
                {
                    deleted++;
                }
            }

            total += deleted;
            // A full batch may hide more. A batch that deleted nothing went to a competing replica, which keeps draining;
            // stopping then also ends the loop on any document that cannot be deleted.
            if (batch.Count < batchSize || deleted == 0)
            {
                return total;
            }
        }
    }

    private async Task<IssuedSession> IssueAsync(UserRecord user, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var token = Tokens.RandomToken(48);
        var session = new SessionRecord(user.InstanceId, user.Id, Tokens.Sha256Hex(token), user.AuthEpoch, clock.GetUtcNow(), expiresAt);
        await store.CreateAsync(AuthStore.SessionCollection, user.InstanceId, session.TokenHash,
            JsonSerializer.SerializeToElement(session, IdentityJson.Default.SessionRecord), cancellationToken).ConfigureAwait(false);
        return new IssuedSession(token, session);
    }

    private async Task<(SessionRecord Session, string Version, UserRecord User)?> ReadLiveAsync(string instanceId, string? token, CancellationToken cancellationToken)
    {
        if (token is not { Length: > 0 and <= MaxTokenLength })
        {
            return null;
        }

        var hash = Tokens.Sha256Hex(token);
        var document = await store.GetAsync(AuthStore.SessionCollection, instanceId, hash, cancellationToken).ConfigureAwait(false);
        var session = document?.Document.Deserialize(IdentityJson.Default.SessionRecord);
        if (session is null || session.Revoked || session.InstanceId != instanceId || !Tokens.FixedTimeEquals(session.TokenHash, hash)
            || session.ExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        var user = await users.GetAsync(instanceId, session.UserId, cancellationToken).ConfigureAwait(false);
        // A lookup that completes after expiry authorizes nothing.
        return user is not null && user.AuthEpoch == session.AuthEpoch && session.ExpiresAt > clock.GetUtcNow() ? (session, document!.Version, user) : null;
    }

    private Task<bool> TryRevokeAsync(SessionRecord session, string version, CancellationToken cancellationToken) =>
        store.CompareAndPutAsync(AuthStore.SessionCollection, session.InstanceId, session.TokenHash,
            JsonSerializer.SerializeToElement(session with { Revoked = true }, IdentityJson.Default.SessionRecord), version, cancellationToken);
}
