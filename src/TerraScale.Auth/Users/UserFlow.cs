namespace TerraScale.Auth.Users;

using System.Net;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;

internal static class UserFlow
{
    internal static readonly SemaphoreSlim[] TokenConsumeGates =
        Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    internal static IResult AccountPage(string slug, User user, string currentSessionId, string? error = null)
    {
        var verified = user.EmailVerified ? "✓ email verified" : "email not verified";
        return Ui.Page("Your account", $"""
            <h1>Your account</h1>
            {ErrorDiv(error)}
            <div class="list">
            <div class="row"><span>{Ui.Esc(user.Name ?? user.Email ?? "user")}</span><span class="muted">{verified}</span></div>
            <div class="row"><span>{Ui.Esc(user.Email)}</span><span class="muted">email</span></div>
            </div>
            <form method="post" action="/ui/{slug}/account/password">
            <label for="cp-current">Current password</label>
            <input id="cp-current" name="current" type="password" required>
            <label for="cp-new">New password</label>
            <input id="cp-new" name="new" type="password" minlength="8" required>
            <button type="submit">Change password</button>
            </form>
            <div class="list">
            <div class="row"><a href="/ui/{slug}/account/sessions">Active sessions</a><span class="muted">devices</span></div>
            <div class="row"><a href="/ui/{slug}/mfa/enroll">Multi-factor authentication</a><span class="muted">coming soon</span></div>
            </div>
            <form method="post" action="/ui/{slug}/account/sessions/revoke-all">
            <button type="submit" class="secondary">Sign out everywhere</button>
            </form>
            <form method="post" action="/ui/{slug}/sign-out">
            <button type="submit" class="secondary">Sign out</button>
            </form>
            """);
    }

    internal static IResult CheckEmailPage(string slug, Instance brand, string email, string what, int minutes) =>
        Ui.Page("Check your email", $"""
            <h1>Check your email</h1>
            <div class="success">If an account exists for {Ui.Esc(email)}, a {what} is on its way.</div>
            <p class="muted">The link expires in {minutes} minutes and can only be used once.</p>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);

    internal static IResult DonePage(string slug, Instance brand, string title, string message) =>
        Ui.Page(title, $"""
            <h1>{Ui.Esc(title)}</h1>
            <div class="success">{Ui.Esc(message)}</div>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);

    internal static IResult InvalidLinkPage(string slug, Instance brand) =>
        Ui.Page("Invalid link", $"""
            <h1>This link is no longer valid</h1>
            <div class="error">This link is invalid, expired, or already used.</div>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a> · <a href="/ui/{slug}/forgot-password">Request a new one</a></p>
            """, status: 400, themeColor: brand.ThemeColor, logoUrl: brand.LogoUrl);

    internal static IResult NotFoundPage() =>
        Ui.Page("Not found", """
            <h1>Page not found</h1>
            <p class="muted">No ts-auth instance lives at this address.</p>
            <p class="hint"><a href="/">Start over</a></p>
            """, status: 404);

    internal static string ErrorDiv(string? error) => error is null ? "" : $"""<div class="error">{Ui.Esc(error)}</div>""";

    // ---- Shared plumbing --------------------------------------------------------------

    internal static Task<Instance?> PageInstanceAsync(AuthStore store, string slug, CancellationToken ct) =>
        store.FindInstanceBySlugAsync(slug, ct);

    internal static async Task<(Session Session, User User)?> ResolvePageSessionAsync(HttpRequest request, SessionService sessions, string instanceId, CancellationToken ct) =>
        request.Cookies[SessionCookies.Name] is { Length: > 0 } token
            ? await sessions.ResolveAsync(instanceId, token, ct)
            : null;

    internal static IResult RedirectToSignIn(string slug, string? next = null) =>
        Results.Redirect(next is null ? $"/ui/{slug}/sign-in" : $"/ui/{slug}/sign-in?next={Uri.EscapeDataString(next)}");

    internal static async Task EstablishSessionAsync(AuthStore store, SessionService sessions, HttpContext context, Instance instance, User user, string method, CancellationToken ct)
    {
        var (_, token) = await sessions.CreateSessionAsync(
            instance.Id, user.Id,
            context.Request.Headers.UserAgent.ToString(),
            context.Connection.RemoteIpAddress?.ToString(), ct);
        context.Response.Cookies.Append(SessionCookies.Name, token, SessionCookies.Options);
        await store.UpsertUserAsync(user with { LastSignInAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, ct);
        await AuditAsync(store, instance.Id, user.Id, "user.signed_in", new Dictionary<string, object>
        {
            ["user_id"] = user.Id,
            ["method"] = method,
        }, ct);
    }

    internal static async Task RevokeSessionsAsync(AuthStore store, string userId, string? except, CancellationToken ct)
    {
        foreach (var session in await store.ListSessionsByUserAsync(userId, ct))
            if (session.Status == "active" && session.Id != except)
                await store.UpsertSessionAsync(session with { Status = "revoked" }, ct);
    }

    internal static async Task<string> IssueTokenAsync(AuthStore store, string instanceId, string userId, string purpose, TimeSpan ttl, CancellationToken ct)
    {
        var token = SecureTokens.RandomToken(40);
        var now = DateTimeOffset.UtcNow;
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = purpose,
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = now.Add(ttl).ToUnixTimeSeconds(),
            CreatedAt = now.ToUnixTimeSeconds(),
        }, ct);
        return token;
    }

    /// <summary>Single-use consume: validates instance, purpose, expiry, and consumed flag, then marks consumed atomically (compare-and-set), so a double request can never consume twice.</summary>
    internal static async Task<VerificationToken?> ConsumeTokenAsync(AuthStore store, string instanceId, string token, string purpose, CancellationToken ct)
    {
        var tokenKey = SecureTokens.Sha256Hex(token);
        var gate = TokenConsumeGates[unchecked((uint)tokenKey.GetHashCode()) % (uint)TokenConsumeGates.Length];
        await gate.WaitAsync(ct);
        try
        {
            return await store.ConsumeVerificationTokenAsync(tokenKey, record =>
                record.InstanceId == instanceId
                && record.Purpose == purpose
                && !record.Consumed
                && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static Task AuditAsync(AuthStore store, string instanceId, string? userId, string type, Dictionary<string, object>? data = null, CancellationToken ct = default) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = userId,
            Data = data ?? [],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);

    internal static string Link(AuthOptions options, string path, string token) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}{path}?token={token}";
}
