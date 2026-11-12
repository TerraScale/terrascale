using Microsoft.EntityFrameworkCore;
using NUlid;
using NUlid.Rng;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed record AccountLockRecord(string InstanceId, string UserId, int Failures, DateTimeOffset? LockedUntil, string? UnlockHash);

public sealed record KnownDeviceRecord(string InstanceId, string UserId, string Fingerprint, DateTimeOffset LastSeen);

public sealed class AccountProtection(AuthData data, ProjectAuthSettings projects, SignInMail mail, AuthOptions options, TimeProvider clock)
{
    public async Task<bool> PasswordAttemptAsync(UserRecord user, bool valid, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!Tenancy.IsProject(user.InstanceId) || !(await projects.GetAsync(user.InstanceId, cancellationToken)).AttackProtection.AccountLockout)
        {
            return valid;
        }

        using var reservation = mail.Reserve();
        var current = await data.ReadAsync((db, token) => db.AccountLocks
            .Where(row => row.InstanceId == user.InstanceId && row.UserId == user.Id).WithVersion().SingleOrDefaultAsync(token), cancellationToken);
        var now = clock.GetUtcNow();
        if (current?.Value.LockedUntil > now)
        {
            return false;
        }

        var failures = valid ? 0 : current is { Value.LockedUntil: null } ? current.Value.Failures + 1 : 1;
        var unlock = failures >= 10 ? Ulid.NewUlid(now, new CSUlidRng()).ToString() : null;
        var updated = new AccountLockRecord(user.InstanceId, user.Id, failures, unlock is null ? null : now.AddMinutes(30),
            unlock is null ? null : Tokens.Sha256Hex(unlock));
        if (!await data.SaveAsync([Change.Assert(user), current is null ? Change.Insert(updated) : Change.Update(current, updated)], cancellationToken))
        {
            throw ApiException.Unavailable();
        }

        if (unlock is not null)
        {
            var link = new Uri(options.PublicOrigin, $"v1/{user.InstanceId}/attack-protection/unlock?user={user.Id}&token={unlock}");
            await reservation.EnqueueEmailAsync(AuthEmailKind.Lockout, new AuthEmailValues(user.Email)
            {
                Tenant = user.InstanceId,
                LockoutUntil = updated.LockedUntil,
                Link = link,
                ExpiresIn = TimeSpan.FromMinutes(30),
            }, locale);
        }

        return valid;
    }

    public async Task<bool> UnlockAsync(string tenant, string userId, string token, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(tenant) || !TerraScaleIds.IsValid(userId) || !TerraScaleIds.IsValid(token))
        {
            return false;
        }

        var hash = Tokens.Sha256Hex(token);
        var current = await data.ReadAsync((db, ct) => db.AccountLocks
            .Where(row => row.InstanceId == tenant && row.UserId == userId).WithVersion().SingleOrDefaultAsync(ct), cancellationToken);
        return current is { Value.UnlockHash: { } expected } && expected == hash && current.Value.LockedUntil > clock.GetUtcNow()
            && await data.SaveAsync(Change.Update(current, current.Value with { Failures = 0, LockedUntil = null, UnlockHash = null }), cancellationToken);
    }

    public async Task SignedInAsync(HttpContext context, string tenant, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Tenancy.IsProject(tenant) || !(await projects.GetAsync(tenant, cancellationToken)).AttackProtection.NewDeviceEmail)
        {
            return;
        }

        var cookieName = "__Host-ts-device-" + tenant;
        var device = context.Request.Cookies[cookieName];
        if (device is not { Length: 43 })
        {
            device = Tokens.RandomToken(43);
        }

        var ip = context.Connection.RemoteIpAddress?.ToString();
        var agent = context.Request.Headers.UserAgent.ToString();
        var fingerprint = Tokens.Sha256Hex(device + "\n" + ip + "\n" + agent);
        var current = await data.ReadAsync((db, token) => db.KnownDevices
            .Where(row => row.InstanceId == tenant && row.UserId == userId && row.Fingerprint == fingerprint).WithVersion().SingleOrDefaultAsync(token), cancellationToken);
        var now = clock.GetUtcNow();
        using var reservation = mail.Reserve();
        var updated = new KnownDeviceRecord(tenant, userId, fingerprint, now);
        var notify = current is null || current.Value.LastSeen <= now.AddDays(-90);
        if (await data.SaveAsync(current is null ? Change.Insert(updated) : Change.Update(current, updated), cancellationToken) && notify)
        {
            var email = await data.ReadAsync((db, token) => db.Users.Where(row => row.Id == userId && row.InstanceId == tenant)
                .Select(row => row.Email).SingleAsync(token), cancellationToken);
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
}
