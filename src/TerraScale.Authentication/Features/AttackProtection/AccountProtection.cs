using System.Security.Cryptography;
using Dapper;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed record AccountLockRecord(string InstanceId, string Subject, string? UserId, int Failures, DateTimeOffset? LockedUntil, string? UnlockHash, DateTimeOffset LastAttempt)
{
    public string? Version { get; init; }
}

public sealed record KnownDeviceRecord(string InstanceId, string UserId, string Fingerprint, DateTimeOffset LastSeen)
{
    public string? Version { get; init; }
}

public sealed class AccountProtection(AuthData data, ProjectAuthSettings projects, SignInMail mail, AuthOptions options, TimeProvider clock, AttemptLimiter limiter)
{
    public async Task<bool> PasswordAttemptAsync(string tenant, string email, UserRecord? user, bool valid, string locale, CancellationToken cancellationToken)
    {
        if (!Tenancy.IsProject(tenant) || !(await projects.GetAsync(tenant, cancellationToken)).AttackProtection.AccountLockout)
        {
            return valid;
        }

        var subject = limiter.Fold(email);
        using var reservation = mail.Reserve();
        for (var round = 0; round < 3; round++)
        {
            var current = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AccountLockRecord>(
                "SELECT * FROM account_locks WHERE instance_id = @tenant AND subject = @subject", new { tenant, subject }), cancellationToken));
            var now = clock.GetUtcNow();
            var locked = current?.Value.LockedUntil > now;
            var failures = locked ? 10 : valid ? 0 : current is { Value.LockedUntil: null } ? current.Value.Failures + 1 : 1;
            var unlock = !locked && failures >= 10 ? SecureUlid(now) : null;
            var updated = new AccountLockRecord(tenant, subject, user?.Id, failures,
                locked ? current!.Value.LockedUntil : unlock is null ? null : now.AddMinutes(30),
                locked ? current!.Value.UnlockHash : unlock is null ? null : Tokens.Sha256Hex(unlock), now);
            var write = current is null ? Change.Insert(updated)
                : valid && current is { Value: { Failures: 0, LockedUntil: null } } ? Change.Assert(current)
                : Change.Update(current, updated);
            if (!await data.SaveAsync(user is null ? [write] : [Change.Assert(user), write], cancellationToken))
            {
                continue;
            }

            if (unlock is not null && user is not null)
            {
                var link = new Uri(options.PublicOrigin, $"v1/{tenant}/attack-protection/unlock?user={user.Id}&token={unlock}");
                await reservation.EnqueueEmailAsync(AuthEmailKind.Lockout, new AuthEmailValues(user.Email)
                {
                    Tenant = tenant,
                    LockoutUntil = updated.LockedUntil,
                    Link = link,
                    ExpiresIn = TimeSpan.FromMinutes(30),
                }, locale);
            }

            return valid && !locked;
        }

        return false;
    }

    public async Task<bool> UnlockAsync(string tenant, string userId, string token, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(tenant) || !TerraScaleIds.IsValid(userId) || !TerraScaleIds.IsValid(token))
        {
            return false;
        }

        var hash = Tokens.Sha256Hex(token);
        var current = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<AccountLockRecord>(
            "SELECT * FROM account_locks WHERE instance_id = @tenant AND user_id = @userId AND unlock_hash = @hash", new { tenant, userId, hash }), cancellationToken));
        return current is { Value.UnlockHash: { } expected } && expected == hash && current.Value.LockedUntil > clock.GetUtcNow()
            && await data.SaveAsync(Change.Update(current, current.Value with { Failures = 0, LockedUntil = null, UnlockHash = null }), cancellationToken);
    }

    public async Task SignedInAsync(HttpContext context, string tenant, string userId, CancellationToken cancellationToken, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Tenancy.IsProject(tenant))
        {
            return;
        }

        var cookieName = "__Host-ts-device-" + tenant;
        var device = context.Items[cookieName] as string ?? context.Request.Cookies[cookieName];
        if (device is not { Length: 43 })
        {
            device = Tokens.RandomToken(43);
        }

        context.Items[cookieName] = device;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var agent = context.Request.Headers.UserAgent.ToString();
        var fingerprint = Tokens.Sha256Hex(device);
        var current = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<KnownDeviceRecord>(
            "SELECT * FROM known_devices WHERE instance_id = @tenant AND user_id = @userId AND fingerprint = @fingerprint", new { tenant, userId, fingerprint }),
            cancellationToken));
        var now = clock.GetUtcNow();
        using var reservation = mail.Reserve();
        var updated = new KnownDeviceRecord(tenant, userId, fingerprint, now);
        var sendEmail = notify && (current is null || current.Value.LastSeen <= now.AddDays(-90))
            && (await projects.GetAsync(tenant, cancellationToken)).AttackProtection.NewDeviceEmail;
        if (await data.SaveAsync(current is null ? Change.Insert(updated) : Change.Update(current, updated), cancellationToken) && sendEmail)
        {
            var email = await data.ReadAsync(connection => connection.QuerySingleAsync<string>(
                "SELECT email FROM users WHERE id = @userId AND instance_id = @tenant", new { userId, tenant }), cancellationToken);
            await reservation.EnqueueEmailAsync(AuthEmailKind.NewDevice, new AuthEmailValues(email)
            {
                Tenant = tenant,
                Device = agent.Length > 256 ? agent[..256] : agent,
                Location = ip,
                Time = now,
            }, AuthEmails.Locale(context.Request));
        }

        context.Response.Cookies.Append(cookieName, device, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(90),
            IsEssential = true,
        });
    }

    public Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken)
    {
        var stale = clock.GetUtcNow().AddDays(-90);
        var idle = clock.GetUtcNow().AddHours(-24);
        return data.InBatchesAsync(batchSize, async connection =>
        {
            var now = clock.GetUtcNow();
            return await connection.ExecuteAsync(
                """
                DELETE FROM known_devices WHERE (instance_id, user_id, fingerprint) IN (
                    SELECT instance_id, user_id, fingerprint FROM known_devices WHERE last_seen <= @stale ORDER BY last_seen LIMIT @batchSize)
                """, new { stale, batchSize })
                + await connection.ExecuteAsync(
                    """
                    DELETE FROM account_locks WHERE (instance_id, subject) IN (
                        SELECT instance_id, subject FROM account_locks WHERE last_attempt <= @idle AND (locked_until IS NULL OR locked_until <= @now)
                        ORDER BY last_attempt LIMIT @batchSize)
                    """, new { idle, now, batchSize });
        }, cancellationToken);
    }

    private static string SecureUlid(DateTimeOffset now)
    {
        Span<byte> randomness = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomness);
        return Ulid.NewUlid(now, randomness).ToString();
    }
}
