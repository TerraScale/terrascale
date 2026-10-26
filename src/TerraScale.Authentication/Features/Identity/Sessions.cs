using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Features.Identity;

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
/// authentication epoch advances. Every check reads the regional TerraBase copy; revocation and losing branches take effect after replication.
/// Callers authorize the instance before using these operations.
/// </summary>
public sealed class Sessions(AuthStore store, Users users, TotpCredentials totp, TimeProvider clock)
{
    /// <summary>Absolute session lifetime from authentication.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    // Issued tokens are 48 characters; anything far longer is never ours and is not hashed or looked up.
    private const int MaxTokenLength = 256;

    private readonly ProjectAuthSettings settings = new(store);

    /// <summary>The project's absolute lifetime, or the platform's fixed lifetime.</summary>
    public async Task<TimeSpan> GetLifetimeAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Tenancy.IsProject(instanceId)
            ? TimeSpan.FromSeconds((await this.settings.GetAsync(instanceId, cancellationToken).ConfigureAwait(false)).Configuration?.SessionLifetimeSeconds
                ?? ConfigurationBody.Default.SessionLifetimeSeconds)
            : Lifetime;

    /// <summary>
    /// Opens a session; refuses stale authentication, and an active factor unless <see cref="UserRecord.MultiFactor"/>
    /// says this sign-in proved it.
    /// </summary>
    public async Task<IssuedSession> CreateAsync(UserRecord user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var lifetime = await this.GetLifetimeAsync(user.InstanceId, cancellationToken).ConfigureAwait(false);
        var createdAt = clock.GetUtcNow();
        return await this.IssueAsync(user, createdAt, createdAt + lifetime, cancellationToken).ConfigureAwait(false)
            ?? throw new ApiException(401, "invalid_credentials", "Authentication could not be completed.");
    }

    /// <summary>The live session for <paramref name="token"/> and its user, or <see langword="null"/>.</summary>
    public async Task<(SessionRecord Session, UserRecord User)?> ResolveAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        await this.ReadLiveAsync(instanceId, token, cancellationToken).ConfigureAwait(false) is { } live ? (live.Session, live.User) : null;

    /// <summary>As <see cref="ResolveAsync"/>, with the version of the session document a caller's commit can be bound to.</summary>
    internal Task<(SessionRecord Session, string Version, UserRecord User)?> ResolveVersionedAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        this.ReadLiveAsync(instanceId, token, cancellationToken);

    /// <summary>
    /// Asserts, inside a caller's transaction, that <paramref name="session"/> is still stored unrevoked, unexpired at
    /// <paramref name="now"/> and at the authentication state it was opened with: a sign-out that lands before the commit fails it.
    /// </summary>
    internal static StoreOperation Live(SessionRecord session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        var live = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", session.InstanceId) },
                new JsonObject { ["eq"] = new JsonArray("/userId", session.UserId) },
                new JsonObject { ["eq"] = new JsonArray("/tokenHash", session.TokenHash) },
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", session.AuthEpoch) },
                new JsonObject { ["ne"] = new JsonArray("/revoked", true) },
                // Expiries are written from UTC instants, so their ISO 8601 text orders chronologically.
                new JsonObject { ["gt"] = new JsonArray("/expiresAt", JsonSerializer.SerializeToNode(now, IdentityJson.Default.DateTimeOffset)) }),
        };
        if (session.AuthStamp is not null)
        {
            ((JsonArray)live["and"]!).Add(new JsonObject { ["eq"] = new JsonArray("/authStamp", session.AuthStamp) });
        }

        using var filter = JsonDocument.Parse(live.ToJsonString());
        return AuthStore.Require(AuthStore.SessionCollection, session.InstanceId, session.TokenHash, filter.RootElement.Clone());
    }

    /// <summary>
    /// Replaces a live session with a new token after a privilege change. The old token stops working first, so
    /// concurrent regional rotations converge to one usable successor; the successor keeps the old expiry.
    /// Returns <see langword="null"/> when the session is not live or another rotation or sign-out won.
    /// </summary>
    public async Task<IssuedSession?> RotateAsync(string instanceId, string? token, CancellationToken cancellationToken = default)
    {
        if (await this.ReadLiveAsync(instanceId, token, cancellationToken).ConfigureAwait(false) is not { } live)
        {
            return null;
        }

        if (live.Session.Bindings.Count >= AuthStore.MaxCredentialBindings)
        {
            throw new ApiException(401, "reauthentication_required", "This sign-in reached its rotation limit. Sign in again to continue.");
        }

        var binding = AuthStore.NewCredentialBinding("session", live.Session.TokenHash);
        if (!await this.TryRevokeAsync(live.Session, live.Version, binding, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var successor = await this.IssueAsync(live.User with { AuthenticationBindings = [.. live.Session.Bindings, binding] }, live.Session.CreatedAt, live.Session.ExpiresAt, cancellationToken).ConfigureAwait(false);
        // A rotation that completes after the absolute expiry hands out nothing.
        return successor is not null && successor.Session.ExpiresAt > clock.GetUtcNow() ? successor : null;
    }

    /// <summary>Signs one session out. True once the session is revoked, including when it already was; false once pruned.</summary>
    public async Task<bool> RevokeAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        token is { Length: > 0 and <= MaxTokenLength } && await this.RevokeStoredAsync(instanceId, Tokens.Sha256Hex(token), cancellationToken).ConfigureAwait(false);

    /// <summary>Signs out the session stored under <paramref name="tokenHash"/>; as <see cref="RevokeAsync"/>.</summary>
    public async Task<bool> RevokeStoredAsync(string instanceId, string tokenHash, CancellationToken cancellationToken = default)
    {
        // A concurrent revocation or policy reduction can change the version; reread before retrying.
        for (var round = 0; round < 2; round++)
        {
            var document = await store.GetAsync(AuthStore.SessionCollection, instanceId, tokenHash, cancellationToken).ConfigureAwait(false);
            var session = document?.Document.Deserialize(IdentityJson.Default.SessionRecord);
            if (session is null || session.InstanceId != instanceId)
            {
                return false;
            }

            if (session.Revoked || await this.TryRevokeAsync(session, document!.Version, AuthStore.NewCredentialBinding("session", session.TokenHash), cancellationToken).ConfigureAwait(false))
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

            var advanced = JsonSerializer.SerializeToElement(current.User with { AuthEpoch = current.User.AuthEpoch + 1, AuthStamp = AuthStore.NewId() }, IdentityJson.Default.UserRecord);
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

    private async Task<IssuedSession?> IssueAsync(UserRecord user, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (!await store.AreCredentialsCurrentAsync(user.InstanceId, user.AuthenticationBindings, cancellationToken).ConfigureAwait(false)
            || !user.MultiFactor && !await totp.AllowsPrimaryAuthenticationAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var token = Tokens.RandomToken(48);
        var session = new SessionRecord(user.InstanceId, user.Id, Tokens.Sha256Hex(token), user.AuthEpoch, createdAt, expiresAt) { Bindings = user.AuthenticationBindings, AuthStamp = user.AuthStamp, Mfa = user.MultiFactor };
        var epoch = new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["eq"] = new JsonArray("/instanceId", user.InstanceId) },
                new JsonObject { ["eq"] = new JsonArray("/id", user.Id) },
                new JsonObject { ["eq"] = new JsonArray("/authEpoch", user.AuthEpoch) },
                new JsonObject { ["eq"] = new JsonArray("/authStamp", user.AuthStamp) },
                // A disabled account opens no session.
                new JsonObject { ["ne"] = new JsonArray("/disabled", true) }),
        };
        using var filter = JsonDocument.Parse(epoch.ToJsonString());
        // Activation, reset and global revocation must not race a primary-only session into storage.
        return await store.TransactAsync(
        [
            // A pending account closure temporarily blocks new sign-ins; cancellation restores them without changing credentials.
            AuthStore.Absent(AuthStore.ClosureJobCollection, user.InstanceId, ClosureJob.Key("account", user.Id)),
            AuthStore.Require(AuthStore.UserCollection, user.InstanceId, user.Id, filter.RootElement),
            AuthStore.Create(AuthStore.SessionCollection, user.InstanceId, session.TokenHash,
                JsonSerializer.SerializeToElement(session, IdentityJson.Default.SessionRecord)),
        ], cancellationToken).ConfigureAwait(false) is null ? new IssuedSession(token, session) : null;
    }

    private async Task<(SessionRecord Session, string Version, UserRecord User)?> ReadLiveAsync(string instanceId, string? token, CancellationToken cancellationToken)
    {
        if (token is not { Length: > 0 and <= MaxTokenLength })
        {
            return null;
        }

        var hash = Tokens.Sha256Hex(token);
        var lifetime = await this.GetLifetimeAsync(instanceId, cancellationToken).ConfigureAwait(false);
        for (var round = 0; round < 3; round++)
        {
            var document = await store.GetAsync(AuthStore.SessionCollection, instanceId, hash, cancellationToken).ConfigureAwait(false);
            var session = document?.Document.Deserialize(IdentityJson.Default.SessionRecord);
            if (session is null || session.Revoked || session.InstanceId != instanceId || !Tokens.FixedTimeEquals(session.TokenHash, hash)
                || session.ExpiresAt <= clock.GetUtcNow())
            {
                return null;
            }

            var version = document!.Version;
            var policyExpiry = session.CreatedAt + lifetime;
            if (policyExpiry < session.ExpiresAt)
            {
                // Persist even an already elapsed reduction: later policy increases must not revive this cookie.
                session = session with { ExpiresAt = policyExpiry };
                var shortened = await store.ReplaceAsync(AuthStore.SessionCollection, instanceId, hash,
                    JsonSerializer.SerializeToElement(session, IdentityJson.Default.SessionRecord), version, cancellationToken).ConfigureAwait(false);
                if (shortened is null)
                {
                    continue;
                }

                version = shortened;
            }

            var user = await users.GetAsync(instanceId, session.UserId, cancellationToken).ConfigureAwait(false);
            // A lookup that completes after expiry authorizes nothing.
            return user is not null && !user.Disabled && user.AuthEpoch == session.AuthEpoch && user.AuthStamp == session.AuthStamp
                && await store.AreCredentialsCurrentAsync(instanceId, session.Bindings, cancellationToken).ConfigureAwait(false)
                && (session.Mfa || await totp.AllowsPrimaryAuthenticationAsync(user, cancellationToken).ConfigureAwait(false))
                && session.ExpiresAt > clock.GetUtcNow() ? (session, version, user with { AuthenticationBindings = session.Bindings, MultiFactor = session.Mfa }) : null;
        }

        throw ApiException.Unavailable();
    }

    private async Task<bool> TryRevokeAsync(SessionRecord session, string version, CredentialBinding binding, CancellationToken cancellationToken) =>
        await store.TransactAsync(
        [
            AuthStore.CredentialClaim(session.InstanceId, binding),
            AuthStore.Replace(AuthStore.SessionCollection, session.InstanceId, session.TokenHash,
                JsonSerializer.SerializeToElement(session with { Revoked = true }, IdentityJson.Default.SessionRecord), version),
        ], cancellationToken).ConfigureAwait(false) is null;
}
