namespace TerraScale.Auth.Users;

using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;

/// <summary>
/// Session engine shared by the hosted UI and the OAuth flows: issue, resolve,
/// and revoke cookie-backed sessions, plus password and user-creation logic.
/// </summary>
public sealed class SessionService(AuthStore store)
{
    /// <summary>Default hosted-session lifetime.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>Normalizes an email address for uniqueness keys.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Validates an email shape (pragmatic: local@domain.tld, exactly one @).</summary>
    public static bool IsValidEmail(string email) =>
        email.Length is >= 3 and <= 254
        && email.Split('@') is [var local, var domain]
        && local.Length > 0
        && domain.Contains('.')
        && !email.Any(c => char.IsWhiteSpace(c));

    /// <summary>Creates a user with a unique per-instance email/username key, gated on the
    /// plan's user quota — every creation path (hosted sign-up, management, SCIM, SSO
    /// auto-create) funnels through here. Throws <see cref="QuotaExceededException"/> when
    /// the instance is at quota and <see cref="TerraScale.DotNet.TerraScaleApiException"/> on conflict.</summary>
    public async Task<User> CreateUserAsync(string instanceId, string? email, string? username, string? password, string? name, CancellationToken ct = default)
    {
        var billing = await BillingState.CurrentAsync(store, instanceId, ct);
        if (await BillingState.CountUsersAsync(store, instanceId, billing.Quota, ct) >= billing.Quota)
            throw new QuotaExceededException();

        string? emailKey = null;
        if (email is not null)
        {
            if (!IsValidEmail(email))
                throw new InvalidOperationException("invalid email address");
            email = NormalizeEmail(email);
            emailKey = $"{instanceId}:{email}";
        }
        string? usernameKey = username is null ? null : $"{instanceId}:{username.Trim().ToLowerInvariant()}";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var userId = "usr_" + SecureTokens.RandomToken(20);
        var quotaSeat = await store.ClaimQuotaSeatAsync(instanceId, billing.Quota, userId, ct);
        try
        {
            return await store.CreateUserAsync(new User
            {
                Id = userId,
                InstanceId = instanceId,
                Email = email,
                Username = username,
                EmailKey = emailKey,
                UsernameKey = usernameKey,
                PasswordHash = password is { Length: > 0 } ? PasswordHasher.Hash(password) : null,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now,
                PrivateMetadata = new Dictionary<string, object> { ["quotaSeat"] = quotaSeat },
            }, ct);
        }
        catch
        {
            await store.ReleaseQuotaSeatAsync(instanceId, quotaSeat, ct);
            throw;
        }
    }

    /// <summary>Verifies a password against a user's stored hash.</summary>
    public static bool VerifyPassword(User user, string password) =>
        user.PasswordHash is not null && PasswordHasher.Verify(password, user.PasswordHash);

    /// <summary>Hash of the same cost as real passwords, burned on unknown accounts so that
    /// sign-in response timing cannot reveal whether an email is registered.</summary>
    private static readonly string DummyHash = PasswordHasher.Hash("ts-auth timing equalizer");

    /// <summary>Sign-in check: always runs one Argon2id verification, even for unknown,
    /// deleted, or password-less accounts, so timing cannot enumerate accounts.</summary>
    public static bool VerifySignInPassword(User? user, string password)
    {
        if (user is { Deleted: false, PasswordHash: { Length: > 0 } })
            return VerifyPassword(user, password);
        _ = PasswordHasher.Verify(password, DummyHash);
        return false;
    }

    /// <summary>Issues a session and returns the opaque cookie token (only the hash is stored).</summary>
    public async Task<(Session Session, string Token)> CreateSessionAsync(string instanceId, string userId, string? userAgent, string? ipAddress, CancellationToken ct = default)
    {
        var token = SecureTokens.RandomToken(48);
        var now = DateTimeOffset.UtcNow;
        var session = new Session
        {
            Id = "ses_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            TokenKey = SecureTokens.Sha256Hex(token),
            Status = "active",
            CreatedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.Add(Lifetime).ToUnixTimeSeconds(),
            LastActiveAt = now.ToUnixTimeSeconds(),
            UserAgent = userAgent?[..Math.Min(userAgent.Length, 256)],
            IpAddress = ipAddress,
        };
        await store.CreateSessionAsync(session, ct);
        return (session, token);
    }

    /// <summary>Resolves a cookie token to a live (active, unexpired) session + user; null otherwise.</summary>
    public async Task<(Session Session, User User)?> ResolveAsync(string instanceId, string token, CancellationToken ct = default)
    {
        var session = await store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (session is null || session.InstanceId != instanceId || session.Status != "active")
            return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;

        var user = await store.GetUserAsync(session.UserId, ct);
        if (user is null || user.Deleted || user.Banned)
            return null;

        if (session.LastActiveAt is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - session.LastActiveAt > 300)
            await store.TryTouchSessionAsync(session.Id, instanceId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
        return (session, user);
    }

    /// <summary>Revokes a session by id.</summary>
    public async Task RevokeAsync(string instanceId, string sessionId, CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(sessionId, ct);
        if (session is not null && session.InstanceId == instanceId && session.Status == "active")
            await store.UpsertSessionAsync(session with { Status = "revoked" }, ct);
    }
}
