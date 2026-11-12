using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Webhooks;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public sealed record IssuedSession(string Token, SessionRecord Session)
{
    public override string ToString() => nameof(IssuedSession);
}

public sealed class Sessions(AuthData data, Users users, Credentials credentials, ProjectAuthSettings settings, TotpCredentials totp, WebhookOutbox outbox, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    private const int MaxTokenLength = 256;

    public async Task<TimeSpan> GetLifetimeAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Tenancy.IsProject(instanceId)
            ? TimeSpan.FromSeconds((await settings.GetAsync(instanceId, cancellationToken).ConfigureAwait(false)).Configuration?.SessionLifetimeSeconds
                ?? ConfigurationBody.Default.SessionLifetimeSeconds)
            : Lifetime;

    public async Task<IssuedSession> CreateAsync(UserRecord user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var project = Tenancy.IsProject(user.InstanceId)
            ? await settings.GetAsync(user.InstanceId, cancellationToken).ConfigureAwait(false)
            : null;
        var lifetime = project is null ? Lifetime
            : TimeSpan.FromSeconds(project.Configuration?.SessionLifetimeSeconds ?? ConfigurationBody.Default.SessionLifetimeSeconds);
        var createdAt = clock.GetUtcNow();
        var signedIn = await outbox.CreateAsync(user.InstanceId, "user.signed_in", user.Id, user.Email, null, cancellationToken).ConfigureAwait(false);
        return await this.IssueAsync(user, createdAt, createdAt + lifetime, cancellationToken, signedIn).ConfigureAwait(false)
            ?? throw new ApiException(401, "invalid_credentials", "Authentication could not be completed.");
    }

    public async Task<(SessionRecord Session, UserRecord User)?> ResolveAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        await this.ReadLiveAsync(instanceId, token, cancellationToken).ConfigureAwait(false) is { } live ? (live.Session, live.User) : null;

    internal Task<(SessionRecord Session, string Version, UserRecord User)?> ResolveVersionedAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        this.ReadLiveAsync(instanceId, token, cancellationToken);

    internal static Change Live(SessionRecord session, string version, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Revoked || session.ExpiresAt <= now
            ? throw new ApiException(401, "invalid_credentials", "Authentication could not be completed.")
            : Change.Assert(session, version);
    }

    public async Task<IssuedSession?> RotateAsync(string instanceId, string? token, CancellationToken cancellationToken = default)
    {
        if (await this.ReadLiveAsync(instanceId, token, cancellationToken).ConfigureAwait(false) is not { } live)
        {
            return null;
        }

        if (live.Session.Bindings.Count >= Credentials.MaxBindings)
        {
            throw new ApiException(401, "reauthentication_required", "This sign-in reached its rotation limit. Sign in again to continue.");
        }

        var binding = Credentials.New("session", live.Session.TokenHash);
        if (!await this.TryRevokeAsync(live.Session, live.Version, binding, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var successor = await this.IssueAsync(live.User with { AuthenticationBindings = [.. live.Session.Bindings, binding] }, live.Session.CreatedAt, live.Session.ExpiresAt, cancellationToken).ConfigureAwait(false);
        return successor is not null && successor.Session.ExpiresAt > clock.GetUtcNow() ? successor : null;
    }

    public async Task<bool> RevokeAsync(string instanceId, string? token, CancellationToken cancellationToken = default) =>
        token is { Length: > 0 and <= MaxTokenLength } && await this.RevokeStoredAsync(instanceId, Tokens.Sha256Hex(token), cancellationToken).ConfigureAwait(false);

    public async Task<bool> RevokeStoredAsync(string instanceId, string tokenHash, CancellationToken cancellationToken = default)
    {
        for (var round = 0; round < 2; round++)
        {
            if (await this.ReadAsync(instanceId, tokenHash, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            if (current.Value.Revoked || await this.TryRevokeAsync(current.Value, current.Version, Credentials.New("session", current.Value.TokenHash), cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<bool> RevokeAllAsync(string instanceId, string userId, CancellationToken cancellationToken = default)
    {
        long? initial = null;
        for (var round = 0; round < 8; round++)
        {
            if (await users.GetAsync(instanceId, userId, cancellationToken).ConfigureAwait(false) is not { } current)
            {
                return false;
            }

            initial ??= current.AuthEpoch;
            if (current.AuthEpoch > initial)
            {
                return true;
            }

            List<Change> changes = [Change.Update(current with { AuthEpoch = current.AuthEpoch + 1, AuthStamp = AuthIds.New() })];
            if (Tenancy.IsProject(instanceId))
            {
                var now = clock.GetUtcNow();
                var revoked = await data.ReadAsync((db, token) => db.Sessions.Where(session => session.InstanceId == instanceId && session.UserId == userId
                    && !session.Revoked && session.AuthEpoch == current.AuthEpoch && session.ExpiresAt > now).ToListAsync(token), cancellationToken).ConfigureAwait(false);
                changes.AddRange(revoked.Select(session => Change.Insert(outbox.NewEvent(instanceId, "session.revoked", userId, current.Email, sessionId: session.Id))));
            }

            if (await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return await data.InBatchesAsync(batchSize, (db, token) => db.Sessions.Where(session => session.Revoked).OrderBy(session => session.ExpiresAt)
            .Take(batchSize).ExecuteDeleteAsync(token), cancellationToken).ConfigureAwait(false)
            + await data.InBatchesAsync(batchSize, (db, token) => db.Sessions.Where(session => session.ExpiresAt <= now).OrderBy(session => session.ExpiresAt)
                .Take(batchSize).ExecuteDeleteAsync(token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IssuedSession?> IssueAsync(UserRecord user, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken, Change? extra = null)
    {
        if (user.Disabled || await credentials.RequireAsync(user.InstanceId, user.AuthenticationBindings, cancellationToken).ConfigureAwait(false) is not { } lineage
            || !user.MultiFactor && !await totp.AllowsPrimaryAuthenticationAsync(user, cancellationToken).ConfigureAwait(false)
            || await this.ClosingAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var token = Tokens.RandomToken(48);
        var session = new SessionRecord(user.InstanceId, user.Id, Tokens.Sha256Hex(token), user.AuthEpoch, createdAt, expiresAt)
        {
            Id = AuthIds.New(),
            Bindings = user.AuthenticationBindings,
            AuthStamp = user.AuthStamp,
            Mfa = user.MultiFactor,
            MfaMethod = user.MfaMethod,
        };
        List<Change> changes = [.. lineage, Change.Assert(user), Change.Insert(session)];
        if (Tenancy.IsProject(user.InstanceId))
        {
            changes.Add(Change.Insert(outbox.NewEvent(user.InstanceId, "session.created", user.Id, user.Email, sessionId: session.Id)));
        }

        if (extra is not null)
        {
            changes.Add(extra);
        }

        return await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false) ? new IssuedSession(token, session) : null;
    }

    private Task<bool> ClosingAsync(UserRecord user, CancellationToken cancellationToken) =>
        user.InstanceId == OidcIssuer.InstanceId
            ? data.ReadAsync((db, token) => db.ClosureJobs.AnyAsync(job => job.InstanceId == user.InstanceId && job.Scope == "account" && job.SubjectId == user.Id, token), cancellationToken)
            : Task.FromResult(false);

    private Task<Versioned<SessionRecord>?> ReadAsync(string instanceId, string tokenHash, CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.Sessions.Where(session => session.TokenHash == tokenHash && session.InstanceId == instanceId).WithVersion()
            .SingleOrDefaultAsync(token), cancellationToken);

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
            if (await this.ReadAsync(instanceId, hash, cancellationToken).ConfigureAwait(false) is not { } current
                || current.Value.Revoked || !Tokens.FixedTimeEquals(current.Value.TokenHash, hash) || current.Value.ExpiresAt <= clock.GetUtcNow())
            {
                return null;
            }

            var (session, version) = (current.Value, current.Version);
            var policyExpiry = session.CreatedAt + lifetime;
            if (policyExpiry < session.ExpiresAt)
            {
                session = session with { ExpiresAt = policyExpiry };
                var shortened = Change.Update(current, session);
                if (!await data.SaveAsync(shortened, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                version = shortened.Version!;
            }

            var user = await users.GetAsync(instanceId, session.UserId, cancellationToken).ConfigureAwait(false);
            return user is not null && !user.Disabled && user.AuthEpoch == session.AuthEpoch && user.AuthStamp == session.AuthStamp
                && await credentials.AreCurrentAsync(instanceId, session.Bindings, cancellationToken).ConfigureAwait(false)
                && (session.Mfa || await totp.AllowsPrimaryAuthenticationAsync(user, cancellationToken).ConfigureAwait(false))
                && session.ExpiresAt > clock.GetUtcNow() ? (session, version, user with { AuthenticationBindings = session.Bindings, MultiFactor = session.Mfa, MfaMethod = session.MfaMethod }) : null;
        }

        throw ApiException.Unavailable();
    }

    private async Task<bool> TryRevokeAsync(SessionRecord session, string version, CredentialBinding binding, CancellationToken cancellationToken)
    {
        List<Change> changes = [Credentials.Claim(session.InstanceId, binding), Change.Update(session with { Revoked = true }, version)];
        if (Tenancy.IsProject(session.InstanceId))
        {
            var user = await users.GetAsync(session.InstanceId, session.UserId, cancellationToken).ConfigureAwait(false);
            changes.Add(Change.Insert(outbox.NewEvent(session.InstanceId, "session.revoked", session.UserId, user?.Email, sessionId: session.Id)));
        }

        return await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false);
    }
}
