namespace TerraScale.Auth.Passcodes;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;

internal static class PasscodeSupport
{
    // ---- Helpers ---------------------------------------------------------------------

    /// <summary>Issues the session cookie, stamps LastSignInAt, and writes the sign-in audit event.</summary>
    internal static async Task SignInAsync(AuthStore store, SessionService sessions, HttpContext http, string instanceId, string userId, string method, CancellationToken ct)
    {
        var (_, cookieToken) = await sessions.CreateSessionAsync(
            instanceId, userId, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), ct);
        var user = await store.GetUserAsync(userId, ct);
        if (user is not null)
            await store.UpsertUserAsync(user with { LastSignInAt = Now(), UpdatedAt = Now() }, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = "user.signed_in",
            ActorId = userId,
            Data = new Dictionary<string, object> { ["method"] = method },
            CreatedAt = Now(),
        }, ct);
        http.Response.Cookies.Append(SessionCookies.Name, cookieToken, SessionCookies.Options);
    }

    /// <summary>Validates a pending-MFA token without consuming it, returning the user id.</summary>
    internal static async Task<string?> PeekPendingAsync(AuthStore store, string instanceId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var record = await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (record is null || record.InstanceId != instanceId || record.Purpose != "mfa.pending"
            || record.Consumed || record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;
        return record.UserId;
    }

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static IResult NotFoundPage() => Ui.Page("Not found", """
        <h1>Not found</h1>
        <p class="muted">This page does not exist.</p>
        """, status: 404);
}
