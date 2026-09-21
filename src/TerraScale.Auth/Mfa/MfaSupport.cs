namespace TerraScale.Auth.Mfa;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;

internal static class MfaSupport
{
    internal const int RecoveryCodeCount = 10;

    // ---- Shared helpers -------------------------------------------------------------

    /// <summary>Reads one form field; null when the request has no form body. Manual
    /// (not [FromForm]) because form binding would require antiforgery middleware.</summary>
    internal static async Task<string?> FormValueAsync(HttpContext http, string key, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return null;
        var form = await http.Request.ReadFormAsync(ct);
        return form[key].Count == 0 ? null : form[key].ToString();
    }

    /// <summary>Validates a pending-MFA token without consuming it, returning the user id.</summary>
    internal static async Task<string?> PeekPendingAsync(AuthStore store, string instanceId, string token, CancellationToken ct)
    {
        var record = await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (record is null || record.InstanceId != instanceId || record.Purpose != "mfa.pending"
            || record.Consumed || record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;
        return record.UserId;
    }

    internal static async Task<User?> SessionUserAsync(AuthStore store, SessionService sessions, HttpContext http, string instanceId, CancellationToken ct)
    {
        var token = http.Request.Cookies[SessionCookies.Name];
        if (token is null)
            return null;
        return (await sessions.ResolveAsync(instanceId, token, ct))?.User;
    }

    /// <summary>Accepts a post-verification redirect target only when it is a local path (open-redirect protection).</summary>
    internal static string SafeDestination(string slug, string? next) =>
        // Control characters are rejected too: browsers strip them before parsing, "/\t/…" becomes "//…".
        next is not null && next.StartsWith('/', StringComparison.Ordinal) && !next.StartsWith("//", StringComparison.Ordinal)
            && !next.Contains('\\') && !next.Any(char.IsControl)
            ? next
            : $"/ui/{slug}/account";

    internal static string[] NewRecoveryCodes() =>
        Enumerable.Range(0, RecoveryCodeCount).Select(_ => SecureTokens.RandomToken(10)).ToArray();

    internal static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    /// <summary>Resolves the sk_ key (401 on a bad key or a slug outside the key's
    /// instance), checks the user belongs to the instance (404), then runs the handler.</summary>
    internal static async Task<IResult> WithScopeAsync(
        InstanceResolver resolver, HttpRequest request, AuthStore store, string slug, string userId,
        Func<InstanceContext, Task<IResult>> handler, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        var user = await store.GetUserAsync(userId, ct);
        if (user is null || user.InstanceId != context.Instance.Id)
            return ApiResults.NotFound();
        return await handler(context);
    }

    internal static string SignIn(string slug) => $"/ui/{slug}/sign-in";

    internal static string Error(string message) => $"""<div class="error">{Ui.Esc(message)}</div>""";

    internal static string ConfirmForm(string slug, string factorId) => $"""
        <form method="post" action="/ui/{slug}/mfa/enroll/confirm?factor={factorId}">
        <label for="code">Code from your app</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" autofocus required>
        <button>Confirm</button>
        </form>
        """;

    internal static IResult CodeFormPage(string slug, string token, string? next, string? error) => Ui.Page("Two-factor verification", $"""
        <h1>Two-factor verification</h1>
        {(error is null ? "" : Error(error))}
        <form method="post" action="/ui/{slug}/mfa/verify">
        <input type="hidden" name="token" value="{Ui.Esc(token)}">
        <input type="hidden" name="next" value="{Ui.Esc(SafeDestination(slug, next))}">
        <label for="code">Authenticator code</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" autofocus required>
        <button>Verify</button>
        </form>
        <p class="hint">Lost your authenticator? <a href="/ui/{slug}/mfa/recovery">Use a recovery code</a>.</p>
        """);

    internal static IResult ExpiredPage(string slug) => Ui.Page("Sign-in expired", $"""
        <h1>Your sign-in expired</h1>
        {Error("This step is no longer valid — it may have timed out. Please sign in again.")}
        <a href="{SignIn(slug)}"><button class="secondary">Back to sign-in</button></a>
        """);

    internal static IResult NotFoundPage() => Ui.Page("Not found", """
        <h1>Not found</h1>
        <p class="muted">This page does not exist.</p>
        """, status: 404);

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
