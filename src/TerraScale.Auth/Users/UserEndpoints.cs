namespace TerraScale.Auth.Users;

using System.Net;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;

/// <summary>Hosted user-facing UI: sign-up, sign-in, the account console, and the email-token flows.</summary>
public static class UserEndpoints
{
    private static readonly TimeSpan VerifyEmailTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MagicLinkTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ResetPasswordTtl = TimeSpan.FromMinutes(30);

    /// <summary>Maps Users endpoints.</summary>
    public static void Map(WebApplication app)
    {
        var getPost = new[] { HttpMethods.Get, HttpMethods.Post };
        app.MapMethods("/ui/{slug}/sign-up", getPost, SignUpAsync);
        app.MapMethods("/ui/{slug}/sign-in", getPost, SignInAsync);
        app.MapPost("/ui/{slug}/sign-out", SignOutAsync);
        app.MapGet("/ui/{slug}/account", AccountAsync);
        app.MapPost("/ui/{slug}/account/password", ChangePasswordAsync);
        app.MapPost("/ui/{slug}/account/sessions/revoke-all", RevokeAllSessionsAsync);
        app.MapGet("/ui/{slug}/account/sessions", SessionsPageAsync);
        app.MapPost("/ui/{slug}/account/sessions/{id}/revoke", RevokeSessionAsync);
        app.MapPost("/ui/{slug}/magic-link", MagicLinkRequestAsync);
        app.MapGet("/ui/{slug}/magic-link/consume", ConsumeMagicLinkAsync);
        app.MapMethods("/ui/{slug}/forgot-password", getPost, ForgotPasswordAsync);
        app.MapMethods("/ui/{slug}/reset-password", getPost, ResetPasswordAsync);
        app.MapGet("/ui/{slug}/verify-email", VerifyEmailAsync);
    }

    // ---- Handlers -------------------------------------------------------------------

    /// <summary>Auto-joins the user to any organization whose enrollment domain matches their email.</summary>
    private static async Task EnrollByDomainAsync(AuthStore store, string instanceId, User user, CancellationToken ct)
    {
        if (user.Email is null || user.Email.Split('@') is not [_, var domain, ..])
            return;
        foreach (var orgDomain in await store.ListOrgDomainsAsync(instanceId, ct))
        {
            if (string.Equals(orgDomain.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await store.CreateMembershipAsync(new OrgMembership
                    {
                        Id = "mem_" + Crypto.SecureTokens.RandomToken(20),
                        InstanceId = instanceId,
                        OrgId = orgDomain.OrgId,
                        UserId = user.Id,
                        MemberKey = $"{orgDomain.OrgId}:{user.Id}",
                        Role = "member",
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }, ct);
                    await store.CreateAuditEventAsync(new AuditEvent
                    {
                        Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
                        InstanceId = instanceId,
                        Type = "organization.enrolled",
                        ActorId = user.Id,
                        Data = new Dictionary<string, object> { ["org_id"] = orgDomain.OrgId, ["domain"] = orgDomain.Domain },
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    }, ct);
                }
                catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    // Already a member — enrollment is idempotent.
                }
            }
        }
    }

    private static async Task<IResult> SignUpAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, IEmailSender emailSender, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (HttpMethods.IsGet(request.Method))
            return SignUpPage(slug);

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var name = form["name"].ToString().Trim();

        if (!SessionService.IsValidEmail(email))
            return SignUpPage(slug, "Please enter a valid email address.", email, name);
        if (password.Length < 8)
            return SignUpPage(slug, "Password must be at least 8 characters.", email, name);
        if (!await Billing.Quota.AllowsSignUpAsync(store, instance.Id, ct))
            return SignUpPage(slug, "This app has reached its user limit. The owner can raise it by upgrading the plan.", email, name);

        User user;
        try
        {
            // Pre-check because not every deployment enforces the unique emailKey index; the catch covers those that do.
            if (await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct) is not null)
                return SignUpPage(slug, "This email is already registered.", email, name);
            user = await sessions.CreateUserAsync(instance.Id, email, username: null, password, Optional(name), ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return SignUpPage(slug, "This email is already registered.", email, name);
        }

        await AuditAsync(store, instance.Id, user.Id, "user.signed_up");
        await EnrollByDomainAsync(store, instance.Id, user, ct);
        await EstablishSessionAsync(store, sessions, request.HttpContext, instance, user, "password", ct);

        var token = await IssueTokenAsync(store, instance.Id, user.Id, "verify_email", VerifyEmailTtl, ct);
        await emailSender.SendAsync(user.Email!, "Verify your email",
            $"Welcome to {instance.Name}!\n\nConfirm your email address by opening:\n\n{Link(options, $"/ui/{slug}/verify-email", token)}\n\nThis link expires in 24 hours.", ct);

        return Results.Redirect($"/ui/{slug}/account");
    }

    private static async Task<IResult> SignInAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var queryNext = request.Query["next"].ToString();
        if (HttpMethods.IsGet(request.Method))
            return SignInPage(slug, next: queryNext);

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        var password = form["password"].ToString();
        var requested = form["next"].ToString() is { Length: > 0 } formNext ? formNext : queryNext;

        var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct);
        if (!SessionService.VerifySignInPassword(user, password))
            return SignInPage(slug, "Invalid email or password.", email, requested);
        if (user!.Banned)
            return SignInPage(slug, "This account is suspended.", email, requested);

        if (await MfaGate.RequiredForUserAsync(store, user.Id, ct))
        {
            var (pending, _) = await MfaGate.BeginPendingAsync(store, instance.Id, user.Id, ct);
            var safe = SafeNext(requested, $"/ui/{slug}/account", options);
            return Results.Redirect($"/ui/{slug}/mfa/verify?token={pending}&next={Uri.EscapeDataString(safe)}");
        }

        await EstablishSessionAsync(store, sessions, request.HttpContext, instance, user, "password", ct);
        return Results.Redirect(SafeNext(requested, $"/ui/{slug}/account", options));
    }

    private static async Task<IResult> SignOutAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (request.Cookies[SessionCookies.Name] is { Length: > 0 } token
            && await sessions.ResolveAsync(instance.Id, token, ct) is { } pair)
        {
            await sessions.RevokeAsync(instance.Id, pair.Session.Id, ct);
            await AuditAsync(store, instance.Id, pair.User.Id, "user.signed_out");
        }
        request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);
        return Results.Redirect($"/ui/{slug}/sign-in");
    }

    private static async Task<IResult> AccountAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account");
        return AccountPage(slug, current.User, current.Session.Id);
    }

    private static async Task<IResult> ChangePasswordAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account");

        var form = await request.ReadFormAsync(ct);
        var currentPassword = form["current"].ToString();
        var newPassword = form["new"].ToString();

        if (!SessionService.VerifyPassword(current.User, currentPassword))
            return AccountPage(slug, current.User, current.Session.Id, "Your current password is incorrect.");
        if (newPassword.Length < 8)
            return AccountPage(slug, current.User, current.Session.Id, "The new password must be at least 8 characters.");

        await store.UpsertUserAsync(current.User with
        {
            PasswordHash = PasswordHasher.Hash(newPassword),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await RevokeSessionsAsync(store, current.User.Id, except: current.Session.Id, ct);
        await AuditAsync(store, instance.Id, current.User.Id, "user.password_changed");
        return Results.Redirect($"/ui/{slug}/account");
    }

    private static async Task<IResult> RevokeAllSessionsAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account");

        await RevokeSessionsAsync(store, current.User.Id, except: null, ct);
        await AuditAsync(store, instance.Id, current.User.Id, "user.signed_out");
        request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);
        return Results.Redirect($"/ui/{slug}/sign-in");
    }

    private static async Task<IResult> SessionsPageAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account/sessions");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var active = (await store.ListSessionsByUserAsync(current.User.Id, ct))
            .Where(s => s.Status == "active" && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActiveAt ?? s.CreatedAt)
            .ToList();
        return SessionsPage(slug, current.Session.Id, active);
    }

    private static async Task<IResult> RevokeSessionAsync(string slug, string id, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account/sessions");

        var target = await store.GetSessionAsync(id, ct);
        if (target is not null && target.InstanceId == instance.Id && target.UserId == current.User.Id && target.Status == "active")
        {
            await store.UpsertSessionAsync(target with { Status = "revoked" }, ct);
            if (target.Id == current.Session.Id)
                request.HttpContext.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Options);
        }
        return Results.Redirect($"/ui/{slug}/account/sessions");
    }

    private static async Task<IResult> MagicLinkRequestAsync(string slug, HttpRequest request, AuthStore store, IEmailSender emailSender, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        if (SessionService.IsValidEmail(email))
        {
            var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct);
            if (user is not null && !user.Deleted && !user.Banned)
            {
                var token = await IssueTokenAsync(store, instance.Id, user.Id, "magic_link", MagicLinkTtl, ct);
                await emailSender.SendAsync(user.Email!, $"Your {instance.Name} sign-in link",
                    $"Sign in to {instance.Name} by opening:\n\n{Link(options, $"/ui/{slug}/magic-link/consume", token)}\n\nThis link expires in 10 minutes and can only be used once.", ct);
            }
        }
        return CheckEmailPage(slug, email, "sign-in link", minutes: 10);
    }

    private static async Task<IResult> ConsumeMagicLinkAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var record = await ConsumeTokenAsync(store, instance.Id, request.Query["token"].ToString(), "magic_link", ct);
        var user = record?.UserId is { } userId ? await store.GetUserAsync(userId, ct) : null;
        if (user is null || user.Deleted || user.Banned)
            return InvalidLinkPage(slug);

        await EstablishSessionAsync(store, sessions, request.HttpContext, instance, user, "magic_link", ct);
        return Results.Redirect($"/ui/{slug}/account");
    }

    private static async Task<IResult> ForgotPasswordAsync(string slug, HttpRequest request, AuthStore store, IEmailSender emailSender, AuthOptions options, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (HttpMethods.IsGet(request.Method))
            return ForgotPasswordPage(slug);

        var form = await request.ReadFormAsync(ct);
        var email = form["email"].ToString().Trim();
        if (SessionService.IsValidEmail(email))
        {
            var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(email), ct);
            if (user is not null && !user.Deleted && !user.Banned)
            {
                var token = await IssueTokenAsync(store, instance.Id, user.Id, "reset_password", ResetPasswordTtl, ct);
                await emailSender.SendAsync(user.Email!, "Reset your password",
                    $"Reset your {instance.Name} password by opening:\n\n{Link(options, $"/ui/{slug}/reset-password", token)}\n\nThis link expires in 30 minutes and can only be used once. If you didn't request this, ignore this email.", ct);
            }
        }
        return CheckEmailPage(slug, email, "password reset link", minutes: 30);
    }

    private static async Task<IResult> ResetPasswordAsync(string slug, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        string token;
        IFormCollection? form = null;
        if (HttpMethods.IsGet(request.Method))
            token = request.Query["token"].ToString();
        else
        {
            form = await request.ReadFormAsync(ct);
            token = form["token"].ToString();
        }
        if (token.Length == 0)
            return InvalidLinkPage(slug);

        if (form is null)
            return await TokenValidAsync(store, instance.Id, token, "reset_password", ct)
                ? ResetPasswordPage(slug, token)
                : InvalidLinkPage(slug);

        var password = form["password"].ToString();
        if (password.Length < 8)
            return ResetPasswordPage(slug, token, "Password must be at least 8 characters.");

        var record = await ConsumeTokenAsync(store, instance.Id, token, "reset_password", ct);
        var user = record?.UserId is { } userId ? await store.GetUserAsync(userId, ct) : null;
        if (user is null || user.Deleted || user.Banned)
            return InvalidLinkPage(slug);

        await store.UpsertUserAsync(user with
        {
            PasswordHash = PasswordHasher.Hash(password),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await RevokeSessionsAsync(store, user.Id, except: null, ct);
        await AuditAsync(store, instance.Id, user.Id, "password.reset", new Dictionary<string, object> { ["user_id"] = user.Id });
        return DonePage(slug, "Password updated", "Your password has been updated and all sessions were signed out. Sign in with your new password.");
    }

    private static async Task<IResult> VerifyEmailAsync(string slug, HttpRequest request, AuthStore store, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        var record = await ConsumeTokenAsync(store, instance.Id, request.Query["token"].ToString(), "verify_email", ct);
        var user = record?.UserId is { } userId ? await store.GetUserAsync(userId, ct) : null;
        if (user is null)
            return InvalidLinkPage(slug);

        await store.UpsertUserAsync(user with
        {
            EmailVerified = true,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await AuditAsync(store, instance.Id, user.Id, "user.email_verified");
        return DonePage(slug, "Email verified", "Your email address has been verified.");
    }

    // ---- Pages ----------------------------------------------------------------------

    private static IResult SignUpPage(string slug, string? error = null, string email = "", string name = "") =>
        Ui.Page("Create your account", $"""
            <h1>Create your account</h1>
            {ErrorDiv(error)}
            <form method="post">
            <label for="su-email">Email</label>
            <input id="su-email" name="email" type="email" value="{Ui.Esc(email)}" required autofocus>
            <label for="su-password">Password</label>
            <input id="su-password" name="password" type="password" minlength="8" required>
            <label for="su-name">Name <span class="muted">(optional)</span></label>
            <input id="su-name" name="name" value="{Ui.Esc(name)}">
            <button type="submit">Create account</button>
            </form>
            <p class="hint">Already registered? <a href="/ui/{slug}/sign-in">Sign in</a></p>
            """);

    private static IResult SignInPage(string slug, string? error = null, string email = "", string next = "") =>
        Ui.Page("Sign in", $"""
            <h1>Sign in</h1>
            {ErrorDiv(error)}
            <form method="post">
            <input type="hidden" name="next" value="{Ui.Esc(next)}">
            <label for="si-email">Email</label>
            <input id="si-email" name="email" type="email" value="{Ui.Esc(email)}" required autofocus>
            <label for="si-password">Password</label>
            <input id="si-password" name="password" type="password" required>
            <button type="submit">Sign in</button>
            </form>
            <form method="post" action="/ui/{slug}/magic-link">
            <label for="si-magic">Or email a magic link</label>
            <input id="si-magic" name="email" type="email" value="{Ui.Esc(email)}" required>
            <button type="submit" class="secondary">Send magic link</button>
            </form>
            <p class="hint"><a href="/ui/{slug}/passkeys/assert">Sign in with a passkey</a> · <a href="/ui/{slug}/passcode">Sign in with an email code</a></p>
            <p class="hint"><a href="/ui/{slug}/connections/list">Sign in with SSO</a></p>
            <p class="hint"><a href="/ui/{slug}/forgot-password">Forgot your password?</a> · <a href="/ui/{slug}/sign-up">Create an account</a></p>
            """);

    private static IResult AccountPage(string slug, User user, string currentSessionId, string? error = null)
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

    private static IResult SessionsPage(string slug, string currentSessionId, IReadOnlyList<Session> sessions)
    {
        var rows = string.Join("\n", sessions.Select(session =>
        {
            var device = Ui.Esc(string.IsNullOrWhiteSpace(session.UserAgent) ? "Unknown device" : session.UserAgent);
            var current = session.Id == currentSessionId ? " · <strong>this device</strong>" : "";
            return $"""
                <div class="row">
                <div>
                <div>{device}</div>
                <div class="muted">created {Stamp(session.CreatedAt)} · last active {Stamp(session.LastActiveAt ?? session.CreatedAt)}{current}</div>
                </div>
                <form class="inline" method="post" action="/ui/{slug}/account/sessions/{session.Id}/revoke"><button type="submit" class="secondary">Revoke</button></form>
                </div>
                """;
        }));
        return Ui.Page("Active sessions", $"""
            <h1>Active sessions</h1>
            <div class="list">{rows}</div>
            <p class="hint"><a href="/ui/{slug}/account">Back to account</a></p>
            """);
    }

    private static IResult ForgotPasswordPage(string slug) =>
        Ui.Page("Reset your password", $"""
            <h1>Reset your password</h1>
            <p class="muted">Enter your email and we'll send a reset link.</p>
            <form method="post">
            <label for="fp-email">Email</label>
            <input id="fp-email" name="email" type="email" required autofocus>
            <button type="submit">Send reset link</button>
            </form>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """);

    private static IResult ResetPasswordPage(string slug, string token, string? error = null) =>
        Ui.Page("Choose a new password", $"""
            <h1>Choose a new password</h1>
            {ErrorDiv(error)}
            <form method="post">
            <input type="hidden" name="token" value="{Ui.Esc(token)}">
            <label for="rp-password">New password</label>
            <input id="rp-password" name="password" type="password" minlength="8" required autofocus>
            <button type="submit">Update password</button>
            </form>
            """);

    private static IResult CheckEmailPage(string slug, string email, string what, int minutes) =>
        Ui.Page("Check your email", $"""
            <h1>Check your email</h1>
            <div class="success">If an account exists for {Ui.Esc(email)}, a {what} is on its way.</div>
            <p class="muted">The link expires in {minutes} minutes and can only be used once.</p>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """);

    private static IResult DonePage(string slug, string title, string message) =>
        Ui.Page(title, $"""
            <h1>{Ui.Esc(title)}</h1>
            <div class="success">{Ui.Esc(message)}</div>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """);

    private static IResult InvalidLinkPage(string slug) =>
        Ui.Page("Invalid link", $"""
            <h1>This link is no longer valid</h1>
            <div class="error">This link is invalid, expired, or already used.</div>
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a> · <a href="/ui/{slug}/forgot-password">Request a new one</a></p>
            """, status: 400);

    private static IResult NotFoundPage() =>
        Ui.Page("Not found", """
            <h1>Page not found</h1>
            <p class="muted">No ts-auth instance lives at this address.</p>
            <p class="hint"><a href="/">Start over</a></p>
            """, status: 404);

    private static string ErrorDiv(string? error) => error is null ? "" : $"""<div class="error">{Ui.Esc(error)}</div>""";

    // ---- Shared plumbing --------------------------------------------------------------

    private static Task<Instance?> PageInstanceAsync(AuthStore store, string slug, CancellationToken ct) =>
        store.FindInstanceBySlugAsync(slug, ct);

    private static async Task<(Session Session, User User)?> ResolvePageSessionAsync(HttpRequest request, SessionService sessions, string instanceId, CancellationToken ct) =>
        request.Cookies[SessionCookies.Name] is { Length: > 0 } token
            ? await sessions.ResolveAsync(instanceId, token, ct)
            : null;

    private static IResult RedirectToSignIn(string slug, string? next = null) =>
        Results.Redirect(next is null ? $"/ui/{slug}/sign-in" : $"/ui/{slug}/sign-in?next={Uri.EscapeDataString(next)}");

    private static async Task EstablishSessionAsync(AuthStore store, SessionService sessions, HttpContext context, Instance instance, User user, string method, CancellationToken ct)
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

    private static async Task RevokeSessionsAsync(AuthStore store, string userId, string? except, CancellationToken ct)
    {
        foreach (var session in await store.ListSessionsByUserAsync(userId, ct))
            if (session.Status == "active" && session.Id != except)
                await store.UpsertSessionAsync(session with { Status = "revoked" }, ct);
    }

    private static async Task<string> IssueTokenAsync(AuthStore store, string instanceId, string userId, string purpose, TimeSpan ttl, CancellationToken ct)
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
    private static Task<VerificationToken?> ConsumeTokenAsync(AuthStore store, string instanceId, string token, string purpose, CancellationToken ct) =>
        store.ConsumeVerificationTokenAsync(SecureTokens.Sha256Hex(token), record =>
            record.InstanceId == instanceId
            && record.Purpose == purpose
            && !record.Consumed
            && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);

    private static async Task<bool> TokenValidAsync(AuthStore store, string instanceId, string token, string purpose, CancellationToken ct) =>
        await store.FindVerificationTokenAsync(SecureTokens.Sha256Hex(token), ct) is { } record
            && record.InstanceId == instanceId
            && record.Purpose == purpose
            && !record.Consumed
            && record.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Only same-site relative paths or this deployment's own origin are allowed redirect targets.</summary>
    private static string SafeNext(string? next, string fallback, AuthOptions options)
    {
        if (next is { Length: > 0 })
        {
            // Browsers treat "\" as "/" and strip control characters before parsing a URL,
            // so "/\evil" or "/\t/evil" must not pass as site-relative paths.
            if (next.StartsWith('/') && !next.StartsWith("//") && !next.Contains('\\') && !next.Any(char.IsControl))
                return next;
            var origin = options.PublicOrigin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            if (string.Equals(next, origin, StringComparison.OrdinalIgnoreCase)
                || next.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
                return next;
        }
        return fallback;
    }

    private static Task AuditAsync(AuthStore store, string instanceId, string? userId, string type, Dictionary<string, object>? data = null, CancellationToken ct = default) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = userId,
            Data = data ?? [],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);

    private static string Link(AuthOptions options, string path, string token) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}{path}?token={token}";

    private static string Stamp(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC";

    private static string? Optional(string value) => value.Length == 0 ? null : value;
}
