namespace TerraScale.Auth.Mfa;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;

/// <summary>MFA endpoints: TOTP enrollment, the sign-in second step, recovery
/// codes, and the per-user management API.</summary>
public static class MfaEndpoints
{
    private const int RecoveryCodeCount = 10;

    /// <summary>Maps Mfa endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // ---- Enrollment (session required) ------------------------------------------

        app.MapGet("/ui/{slug}/mfa/enroll", async (string slug, HttpContext http, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            if (await SessionUserAsync(store, sessions, http, instance.Id, ct) is null)
                return Results.Redirect(SignIn(slug));

            return Ui.Page("Two-factor authentication", $"""
                <h1>Two-factor authentication</h1>
                <p class="muted">Protect your account with a time-based one-time password (TOTP) app. After enrolling you'll enter a 6-digit code at every sign-in.</p>
                <form method="post" action="/ui/{slug}/mfa/enroll/start">
                <button>Set up authenticator</button>
                </form>
                """);
        });

        app.MapPost("/ui/{slug}/mfa/enroll/start", async (string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
            if (user is null)
                return Results.Redirect(SignIn(slug));

            var factors = await store.ListMfaFactorsAsync(user.Id, ct);
            if (factors.Any(f => f.Type == "totp" && f.Confirmed))
                return Ui.Page("Already enrolled", $"""
                    <h1>Already enrolled</h1>
                    {Error("You already have a confirmed authenticator. Delete it before enrolling a new one.")}
                    <a href="/ui/{slug}/account"><button class="secondary">Back to account</button></a>
                    """, status: 409);

            // One enrollment at a time: drop any abandoned half-finished one.
            foreach (var stale in factors.Where(f => f.Type == "totp" && !f.Confirmed))
                await store.DeleteMfaFactorAsync(stale.Id, ct);

            var secret = RandomNumberGenerator.GetBytes(20);
            var factor = await store.CreateMfaFactorAsync(new MfaFactor
            {
                Id = "mfa_" + SecureTokens.RandomToken(20),
                InstanceId = instance.Id,
                UserId = user.Id,
                Type = "totp",
                SecretBox = SecretBox.Encrypt(options.MasterKey, secret),
                Confirmed = false,
                CreatedAt = Now(),
            }, ct);

            var base32 = Encodings.Base32Encode(secret);
            var account = user.Email ?? user.Id;
            var otpauth = $"otpauth://totp/ts-auth:{Uri.EscapeDataString(account)}?secret={base32}&issuer=ts-auth";
            return Ui.Page("Set up your authenticator", $"""
                <h1>Set up your authenticator</h1>
                <p class="muted">Add the entry below to your authenticator app, then confirm with a code it generates.</p>
                <label>Secret (base32)</label>
                <div class="row"><code>{base32}</code></div>
                <label>Manual entry URI</label>
                <div class="row"><code>{Ui.Esc(otpauth)}</code></div>
                {ConfirmForm(slug, factor.Id)}
                """);
        });

        app.MapPost("/ui/{slug}/mfa/enroll/confirm", async (string slug, string? factor, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
            if (user is null)
                return Results.Redirect(SignIn(slug));

            var record = factor is null ? null : await store.GetMfaFactorAsync(factor, ct);
            if (record is null || record.InstanceId != instance.Id || record.UserId != user.Id || record.Type != "totp" || record.Confirmed)
                return NotFoundPage();
            var code = await FormValueAsync(http, "code", ct);
            if (string.IsNullOrWhiteSpace(code)
                || !Totp.Verify(SecretBox.Decrypt(options.MasterKey, record.SecretBox!), code.Trim(), DateTimeOffset.UtcNow))
                return Ui.Page("Set up your authenticator", $"""
                    <h1>Set up your authenticator</h1>
                    {Error("That code didn't match. Try again.")}
                    {ConfirmForm(slug, record.Id)}
                    """);

            await store.UpsertMfaFactorAsync(record with { Confirmed = true }, ct);
            var codes = NewRecoveryCodes();
            await StoreRecoveryCodesAsync(store, instance.Id, user.Id, codes, ct);
            await AuditAsync(store, instance.Id, "mfa.enrolled", user.Id, new() { ["factorId"] = record.Id, ["type"] = "totp" }, ct);

            var rows = string.Join("\n", codes.Select(c => $"<div class=\"row\"><code>{c}</code></div>"));
            return Ui.Page("Recovery codes", $"""
                <h1>Recovery codes</h1>
                <p class="muted">Two-factor is on. Each code below works once if you ever lose your authenticator. They will not be shown again.</p>
                <div class="list">
                {rows}
                </div>
                <a href="/ui/{slug}/account"><button class="secondary">I saved my codes</button></a>
                """);
        });

        // ---- Second step of sign-in ---------------------------------------------------

        app.MapGet("/ui/{slug}/mfa/verify", async (string slug, string? token, string? next, AuthStore store, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            if (token is null || await PeekPendingAsync(store, instance.Id, token, ct) is null)
                return ExpiredPage(slug);
            return CodeFormPage(slug, token, next, error: null);
        });

        app.MapPost("/ui/{slug}/mfa/verify", async (string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            var token = await FormValueAsync(http, "token", ct);
            var next = await FormValueAsync(http, "next", ct);
            if (token is not { Length: > 0 } || await PeekPendingAsync(store, instance.Id, token, ct) is not { } userId)
                return ExpiredPage(slug);

            var code = await FormValueAsync(http, "code", ct);
            // A wrong guess keeps the pending token alive so the user can retry;
            // guess attempts are capped by the token's 10-minute TTL, not a counter.
            if (string.IsNullOrWhiteSpace(code) || !await MfaGate.VerifyCodeAsync(store, options.MasterKey, userId, code.Trim(), ct))
                return CodeFormPage(slug, token, next, "Invalid code. Try again.");

            await MfaGate.ConsumePendingAsync(store, instance.Id, token, ct);

            var (_, cookieToken) = await sessions.CreateSessionAsync(
                instance.Id, userId, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), ct);
            var user = await store.GetUserAsync(userId, ct);
            if (user is not null)
                await store.UpsertUserAsync(user with { LastSignInAt = Now(), UpdatedAt = Now() }, ct);
            await AuditAsync(store, instance.Id, "user.signed_in", userId, new() { ["method"] = "password+mfa" }, ct);

            http.Response.Cookies.Append(SessionCookies.Name, cookieToken, SessionCookies.Options);
            return Results.Redirect(SafeDestination(slug, next));
        });

        // ---- Recovery codes --------------------------------------------------------------

        app.MapGet("/ui/{slug}/mfa/recovery", async (string slug, HttpContext http, AuthStore store, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
            if (user is null)
                return Results.Redirect(SignIn(slug));

            var hasCodes = (await store.ListMfaFactorsAsync(user.Id, ct))
                .Any(f => f.Type == "recovery" && f.RecoveryCodes is { Length: > 0 });
            if (!hasCodes)
                return Ui.Page("No recovery codes", $"""
                    <h1>No recovery codes left</h1>
                    {Error("You have no unused recovery codes. Ask an administrator to reset your second factor.")}
                    """);

            return Ui.Page("Use a recovery code", $"""
                <h1>Use a recovery code</h1>
                <p class="muted">Lost your authenticator? Enter one of your one-time recovery codes to unlock your account.</p>
                <form method="post" action="/ui/{slug}/mfa/recovery/consume">
                <label for="code">Recovery code</label>
                <input id="code" name="code" autocomplete="off" autofocus required>
                <button>Unlock account</button>
                </form>
                """);
        });

        app.MapPost("/ui/{slug}/mfa/recovery/consume", async (string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            var user = await SessionUserAsync(store, sessions, http, instance.Id, ct);
            if (user is null)
                return Results.Redirect(SignIn(slug));

            var code = await FormValueAsync(http, "code", ct);
            if (string.IsNullOrWhiteSpace(code) || !await MfaGate.VerifyCodeAsync(store, options.MasterKey, user.Id, code.Trim(), ct))
                return Ui.Page("Use a recovery code", $"""
                    <h1>Use a recovery code</h1>
                    {Error("That recovery code is invalid or already used.")}
                    <a href="/ui/{slug}/sign-in"><button class="secondary">Back to sign-in</button></a>
                    """);

            // VerifyCodeAsync consumed the code above; issue a fresh session.
            var (_, cookieToken) = await sessions.CreateSessionAsync(
                instance.Id, user.Id, http.Request.Headers.UserAgent.ToString(), http.Connection.RemoteIpAddress?.ToString(), ct);
            http.Response.Cookies.Append(SessionCookies.Name, cookieToken, SessionCookies.Options);
            return Results.Redirect($"/ui/{slug}/account");
        });

        // ---- Management API (sk_ bearer) ---------------------------------------------------

        app.MapGet("/v1/{slug}/users/{userId}/mfa-factors", async (string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
            await WithScopeAsync(resolver, request, store, slug, userId, async context =>
            {
                var factors = await store.ListMfaFactorsAsync(userId, ct);
                return Results.Ok(new { data = factors.Select(f => new { f.Id, f.Type, f.Confirmed, f.CreatedAt }) });
            }, ct));

        app.MapDelete("/v1/{slug}/users/{userId}/mfa-factors/{id}", async (string slug, string userId, string id, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
            await WithScopeAsync(resolver, request, store, slug, userId, async context =>
            {
                var factor = await store.GetMfaFactorAsync(id, ct);
                if (factor is null || factor.InstanceId != context.Instance.Id || factor.UserId != userId)
                    return ApiResults.NotFound();

                await store.DeleteMfaFactorAsync(id, ct);
                await AuditAsync(store, context.Instance.Id, "mfa.factor_deleted", context.ApiKeyId, new() { ["factorId"] = id, ["userId"] = userId }, ct);
                return Results.NoContent();
            }, ct));

        app.MapPost("/v1/{slug}/users/{userId}/mfa-factors/{id}/regenerate-recovery", async (string slug, string userId, string id, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
            await WithScopeAsync(resolver, request, store, slug, userId, async context =>
            {
                var factor = await store.GetMfaFactorAsync(id, ct);
                if (factor is null || factor.InstanceId != context.Instance.Id || factor.UserId != userId)
                    return ApiResults.NotFound();
                if (factor.Type != "recovery")
                    return ApiResults.BadRequest("invalid_factor", "recovery codes can only be regenerated for a recovery factor");

                var codes = NewRecoveryCodes();
                await store.UpsertMfaFactorAsync(factor with { RecoveryCodes = codes.Select(SecureTokens.Sha256Hex).ToArray() }, ct);
                return Results.Ok(new { data = codes });
            }, ct));
    }

    // ---- Shared helpers -------------------------------------------------------------

    /// <summary>Reads one form field; null when the request has no form body. Manual
    /// (not [FromForm]) because form binding would require antiforgery middleware.</summary>
    private static async Task<string?> FormValueAsync(HttpContext http, string key, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return null;
        var form = await http.Request.ReadFormAsync(ct);
        return form[key].Count == 0 ? null : form[key].ToString();
    }

    /// <summary>Validates a pending-MFA token without consuming it, returning the user id.</summary>
    private static async Task<string?> PeekPendingAsync(AuthStore store, string instanceId, string token, CancellationToken ct)
    {
        var record = await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (record is null || record.InstanceId != instanceId || record.Purpose != "mfa.pending"
            || record.Consumed || record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;
        return record.UserId;
    }

    private static async Task<User?> SessionUserAsync(AuthStore store, SessionService sessions, HttpContext http, string instanceId, CancellationToken ct)
    {
        var token = http.Request.Cookies[SessionCookies.Name];
        if (token is null)
            return null;
        return (await sessions.ResolveAsync(instanceId, token, ct))?.User;
    }

    /// <summary>Accepts a post-verification redirect target only when it is a local path (open-redirect protection).</summary>
    private static string SafeDestination(string slug, string? next) =>
        next is not null && next.StartsWith('/') && !next.StartsWith("//", StringComparison.Ordinal) && !next.Contains('\\')
            ? next
            : $"/ui/{slug}/account";

    private static async Task StoreRecoveryCodesAsync(AuthStore store, string instanceId, string userId, string[] codes, CancellationToken ct)
    {
        var hashed = codes.Select(SecureTokens.Sha256Hex).ToArray();
        var existing = (await store.ListMfaFactorsAsync(userId, ct)).FirstOrDefault(f => f.Type == "recovery");
        if (existing is null)
            await store.CreateMfaFactorAsync(new MfaFactor
            {
                Id = "mfa_" + SecureTokens.RandomToken(20),
                InstanceId = instanceId,
                UserId = userId,
                Type = "recovery",
                RecoveryCodes = hashed,
                Confirmed = true,
                CreatedAt = Now(),
            }, ct);
        else
            await store.UpsertMfaFactorAsync(existing with { RecoveryCodes = hashed }, ct);
    }

    private static string[] NewRecoveryCodes() =>
        Enumerable.Range(0, RecoveryCodeCount).Select(_ => SecureTokens.RandomToken(10)).ToArray();

    private static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    /// <summary>Resolves the sk_ key (401 on a bad key), checks the slug and user match
    /// the key's instance (404), then runs the handler with the instance context.</summary>
    private static async Task<IResult> WithScopeAsync(
        InstanceResolver resolver, HttpRequest request, AuthStore store, string slug, string userId,
        Func<InstanceContext, Task<IResult>> handler, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null)
            return ApiResults.Unauthorized();
        if (context.Instance.Slug != slug)
            return ApiResults.NotFound();
        var user = await store.GetUserAsync(userId, ct);
        if (user is null || user.InstanceId != context.Instance.Id)
            return ApiResults.NotFound();
        return await handler(context);
    }

    private static string SignIn(string slug) => $"/ui/{slug}/sign-in";

    private static string Error(string message) => $"""<div class="error">{Ui.Esc(message)}</div>""";

    private static string ConfirmForm(string slug, string factorId) => $"""
        <form method="post" action="/ui/{slug}/mfa/enroll/confirm?factor={factorId}">
        <label for="code">Code from your app</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" autofocus required>
        <button>Confirm</button>
        </form>
        """;

    private static IResult CodeFormPage(string slug, string token, string? next, string? error) => Ui.Page("Two-factor verification", $"""
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

    private static IResult ExpiredPage(string slug) => Ui.Page("Sign-in expired", $"""
        <h1>Your sign-in expired</h1>
        {Error("This step is no longer valid — it may have timed out. Please sign in again.")}
        <a href="{SignIn(slug)}"><button class="secondary">Back to sign-in</button></a>
        """);

    private static IResult NotFoundPage() => Ui.Page("Not found", """
        <h1>Not found</h1>
        <p class="muted">This page does not exist.</p>
        """, status: 404);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
