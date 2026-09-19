namespace TerraScale.Auth.Users;

using System.Collections.Concurrent;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;

/// <summary>Email passcode first factor: request/verify endpoints, the MFA second
/// step, and the hosted sign-in page.</summary>
public static class PasscodeEndpoints
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResendThrottle = TimeSpan.FromSeconds(30);
    private const int MaxVerifyAttempts = 5;

    // ponytail: in-memory failure counters reset on restart; upgrade path is an
    // attempts column on the verification token record.
    private static readonly ConcurrentDictionary<string, int> FailedAttempts = new(StringComparer.Ordinal);

    /// <summary>Maps passcode endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // ---- Hosted page ------------------------------------------------------------

        app.MapGet("/ui/{slug}/passcode", async (string slug, AuthStore store, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            return instance is null ? NotFoundPage() : Ui.Page("Sign in with a code", PasscodePageBody(slug));
        });

        // ---- Request a code -----------------------------------------------------------

        app.MapPost("/ui/{slug}/passcode/request", async (string slug, HttpContext http, AuthStore store, IEmailSender emailSender, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return ApiResults.NotFound("instance_not_found", "no such instance");

            var body = await http.Request.ReadFromJsonAsync<PasscodeRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Email))
                return ApiResults.BadRequest("invalid_request", "email is required");

            var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(body.Email), ct);
            if (user is not null && !await ThrottledAsync(store, instance.Id, user.Id, ct))
            {
                var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                await store.CreateVerificationTokenAsync(new VerificationToken
                {
                    Id = "vtok_" + SecureTokens.RandomToken(20),
                    InstanceId = instance.Id,
                    UserId = user.Id,
                    Purpose = "passcode",
                    TokenKey = SecureTokens.Sha256Hex(code),
                    ExpiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime).ToUnixTimeSeconds(),
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }, ct);
                await emailSender.SendAsync(user.Email ?? "", "Your ts-auth sign-in code", $"Your ts-auth sign-in code is {code}", ct);
            }
            // Same body whether the user exists, the email is unknown, or sending was
            // throttled — no account enumeration.
            return Results.Ok(new { ok = true });
        });

        // ---- Verify a code ---------------------------------------------------------------

        app.MapPost("/ui/{slug}/passcode/verify", async (string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return ApiResults.NotFound("instance_not_found", "no such instance");

            var body = await http.Request.ReadFromJsonAsync<PasscodeVerifyRequest>(ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Code))
                return ApiResults.BadRequest("invalid_request", "email and code are required");

            var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(body.Email), ct);
            var record = await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(body.Code.Trim()), ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (user is null || record is null || record.InstanceId != instance.Id || record.Purpose != "passcode"
                || record.UserId != user.Id || record.Consumed || record.ExpiresAt <= now)
                return await InvalidCodeAsync(store, instance.Id, user?.Id, ct);

            await store.UpsertVerificationTokenAsync(record with { Consumed = true }, ct);
            FailedAttempts.TryRemove(user.Id, out _);

            if (await MfaGate.RequiredForUserAsync(store, user.Id, ct))
            {
                var (mfaToken, _) = await MfaGate.BeginPendingAsync(store, instance.Id, user.Id, ct);
                return Results.Ok(new { mfa_required = true, mfa_token = mfaToken });
            }

            await SignInAsync(store, sessions, http, instance.Id, user.Id, "passcode", ct);
            return Results.Ok(new { ok = true });
        });

        // ---- MFA second step ---------------------------------------------------------------

        app.MapGet("/ui/{slug}/passcode/mfa", async (string slug, string? token, AuthStore store, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return NotFoundPage();
            return await PeekPendingAsync(store, instance.Id, token, ct) is null
                ? ExpiredPage(slug)
                : Ui.Page("Two-factor verification", MfaFormBody(slug, token!));
        });

        app.MapPost("/ui/{slug}/passcode/mfa", async (string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct) =>
        {
            var instance = await store.FindInstanceBySlugAsync(slug, ct);
            if (instance is null)
                return ApiResults.NotFound("instance_not_found", "no such instance");

            var (token, code) = await ReadMfaSubmissionAsync(http, ct);
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(code))
                return ApiResults.BadRequest("invalid_request", "mfa_token and code are required");

            var userId = await PeekPendingAsync(store, instance.Id, token, ct);
            if (userId is null)
                return Results.Json(new { ok = false, error = "invalid_mfa_token" }, statusCode: 401);

            if (!await MfaGate.VerifyCodeAsync(store, options.MasterKey, userId, code.Trim(), ct))
                return await DelayedInvalidCodeAsync();

            await MfaGate.ConsumePendingAsync(store, instance.Id, token, ct);
            await SignInAsync(store, sessions, http, instance.Id, userId, "passcode+mfa", ct);
            return http.Request.HasFormContentType
                ? Results.Redirect($"/ui/{slug}/account")
                : Results.Ok(new { ok = true });
        });
    }

    // ---- Helpers ---------------------------------------------------------------------

    /// <summary>Issues the session cookie, stamps LastSignInAt, and writes the sign-in audit event.</summary>
    private static async Task SignInAsync(AuthStore store, SessionService sessions, HttpContext http, string instanceId, string userId, string method, CancellationToken ct)
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

    /// <summary>True when the user already received a passcode within the resend window.</summary>
    private static async Task<bool> ThrottledAsync(AuthStore store, string instanceId, string userId, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)ResendThrottle.TotalSeconds;
        var recent = await store.ListVerificationTokensAsync(instanceId, "passcode", 5, ct);
        return recent.Any(t => t.InstanceId == instanceId && t.UserId == userId && t.CreatedAt > cutoff);
    }

    /// <summary>Counts consecutive failures per user; at the cap the user's pending
    /// passcode tokens are marked consumed. Always delayed to blunt brute force.</summary>
    private static async Task<IResult> InvalidCodeAsync(AuthStore store, string instanceId, string? userId, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        if (userId is not null && FailedAttempts.AddOrUpdate(userId, 1, (_, count) => count + 1) >= MaxVerifyAttempts)
        {
            FailedAttempts.TryRemove(userId, out _);
            foreach (var token in (await store.ListVerificationTokensAsync(instanceId, "passcode", 100, ct)).Where(t => t.UserId == userId && !t.Consumed))
                await store.UpsertVerificationTokenAsync(token with { Consumed = true }, ct);
        }
        return Results.Json(new { ok = false, error = "invalid_code" }, statusCode: 401);
    }

    private static async Task<IResult> DelayedInvalidCodeAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        return Results.Json(new { ok = false, error = "invalid_code" }, statusCode: 401);
    }

    /// <summary>Validates a pending-MFA token without consuming it, returning the user id.</summary>
    private static async Task<string?> PeekPendingAsync(AuthStore store, string instanceId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var record = await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct);
        if (record is null || record.InstanceId != instanceId || record.Purpose != "mfa.pending"
            || record.Consumed || record.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;
        return record.UserId;
    }

    /// <summary>The MFA step accepts the hosted form (urlencoded) or a JSON fetch.</summary>
    private static async Task<(string? Token, string? Code)> ReadMfaSubmissionAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            return (FormValue(form, "mfa_token"), FormValue(form, "code"));
        }
        var body = await http.Request.ReadFromJsonAsync<PasscodeMfaRequest>(ct);
        return (body?.MfaToken, body?.Code);
    }

    private static string? FormValue(IFormCollection form, string key) =>
        form[key].Count == 0 ? null : form[key].ToString();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string PasscodePageBody(string slug) => $$"""
        <h1>Sign in with a code</h1>
        <div id="message" class="muted">We'll email you a one-time code.</div>
        <label for="email">Email</label>
        <input id="email" type="email" autocomplete="username" required>
        <button id="send">Send code</button>
        <label for="code">Code</label>
        <input id="code" inputmode="numeric" autocomplete="one-time-code" required>
        <button id="verify">Verify</button>
        <p class="hint"><a href="/ui/{{slug}}/sign-in">Back to sign in</a></p>
        <script>
        const message = document.getElementById('message');
        async function post(path, body) {
            const response = await fetch(path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
            return { ok: response.ok, body: await response.json().catch(() => ({})) };
        }
        document.getElementById('send').onclick = async () => {
            message.textContent = 'Sending…';
            await post('/ui/{{slug}}/passcode/request', { email: document.getElementById('email').value });
            message.textContent = 'If that address has an account, a code is on its way.';
        };
        document.getElementById('verify').onclick = async () => {
            const result = await post('/ui/{{slug}}/passcode/verify', {
                email: document.getElementById('email').value,
                code: document.getElementById('code').value,
            });
            if (result.body.mfa_required)
                window.location = '/ui/{{slug}}/passcode/mfa?token=' + encodeURIComponent(result.body.mfa_token);
            else if (result.body.ok)
                window.location = '/ui/{{slug}}/account';
            else
                message.textContent = 'That code is wrong or has expired.';
        };
        </script>
        """;

    private static string MfaFormBody(string slug, string token) => $$"""
        <h1>Two-factor verification</h1>
        <p class="muted">Enter the code from your authenticator app to finish signing in.</p>
        <form method="post" action="/ui/{{slug}}/passcode/mfa">
        <input type="hidden" name="mfa_token" value="{{Ui.Esc(token)}}">
        <label for="code">Authenticator code</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" autofocus required>
        <button>Verify</button>
        </form>
        <p class="hint"><a href="/ui/{{slug}}/sign-in">Back to sign in</a></p>
        """;

    private static IResult ExpiredPage(string slug) => Ui.Page("Sign-in expired", $"""
        <h1>Your sign-in expired</h1>
        <div class="error">This step is no longer valid — it may have timed out. Please sign in again.</div>
        <a href="/ui/{slug}/sign-in"><button class="secondary">Back to sign-in</button></a>
        """);

    private static IResult NotFoundPage() => Ui.Page("Not found", """
        <h1>Not found</h1>
        <p class="muted">This page does not exist.</p>
        """, status: 404);
}

/// <summary>Passcode request body.</summary>
internal sealed record PasscodeRequest(string? Email);

/// <summary>Passcode verify body.</summary>
internal sealed record PasscodeVerifyRequest(string? Email, string? Code);

/// <summary>Passcode MFA step body.</summary>
internal sealed record PasscodeMfaRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("mfa_token")] string? MfaToken,
    string? Code);
