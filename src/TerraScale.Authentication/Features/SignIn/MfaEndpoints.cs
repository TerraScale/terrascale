using Microsoft.AspNetCore.Http.Metadata;
using QRCoder;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>
/// <c>POST /ui/{slug}/sign-in/mfa</c>: the second sign-in step of an account with an active authenticator app. Redeems a
/// TOTP code or a recovery code against the sealed challenge the first step issued, then opens the session.
/// </summary>
public sealed class MfaSignInEndpoint(Sessions sessions, TotpCredentials mfa, PlatformDirectory platform, HostedAuth hosted, AuthOptions options, ILogger<MfaSignInEndpoint> logger)
    : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/mfa");
        Name("ui_sign_in_mfa");
        Summary("Sign in with a second factor");
        Description("Same-origin form post with challenge (from the code, password or sign-up step), code and next. code is the authenticator app's current six-digit code or one unused 24-character recovery code; each works once. The challenge lasts 5 minutes, completes at most once and dies with a sign-out everywhere, reset or factor change; a spent or expired challenge answers 401 with sign-in from the start. Charges 30 calls per IP per minute and 10 second-factor attempts per account per 15 minutes; exhaustion answers 429 with Retry-After and keeps a live challenge. A wrong code answers 401 with the step again. Success sets the tsauth_session cookie and continues to next.");
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        var ipWait = await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken);
        // Resolving the challenge only reads: a refused request keeps a valid continuation and spends no proof.
        if (await mfa.ChallengedAsync(tenant, form.Challenge, cancellationToken) is not { } challenged)
        {
            AuthMetrics.SignIn("hosted_mfa", ipWait is null ? "expired" : "rate_limited");
            return Expired(tenant, next);
        }

        var user = challenged.User;
        if (ipWait is { } wait)
        {
            AuthMetrics.SignIn("hosted_mfa", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Mfa, wait, user.Email, next, challenge: form.Challenge);
        }

        var code = (form.Code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        bool completed;
        try
        {
            completed = await mfa.CompleteAsync(challenged, code, cancellationToken);
        }
        catch (ApiException limited) when (limited is { Status: StatusCodes.Status429TooManyRequests, RetryAfter: { } accountWait })
        {
            AuthMetrics.SignIn("hosted_mfa", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Mfa, accountWait, user.Email, next, challenge: form.Challenge);
        }

        if (!completed)
        {
            AuthMetrics.SignIn("hosted_mfa", "refused");
            var wrong = new SignInPageModel(SignInStep.Mfa, tenant, user.Email, next, "Enter the current code from your authenticator app, or an unused recovery code.")
            {
                ErrorTitle = "That code is wrong or already used",
                Challenge = form.Challenge,
            };
            return SignInPageModel.Render(this.HttpContext, wrong, StatusCodes.Status401Unauthorized);
        }

        // The challenge is spent; a session that loses to a newer sign-out or a competing completion starts over.
        if (await SignInFlow.OpenSessionAsync(sessions, user with { MultiFactor = true }, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_mfa", "expired");
            return Expired(tenant, next);
        }

        if (code.Length != 6)
        {
            logger.ZLogInformation($"Audit: user {user.Id:@UserId} signed in with a recovery code");
            await SecurityFlow.AuditAsync(platform, tenant, user.Id, "member.mfa_recovery_code_used", cancellationToken);
        }

        AuthMetrics.SignIn("hosted_mfa", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }

    private IResult Expired(string tenant, string? next) =>
        SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Email, tenant, Next: next, Error: "Your sign-in took too long, was already completed or your account changed. Sign in again.")
        {
            ErrorTitle = "Sign-in expired",
        }, StatusCodes.Status401Unauthorized);
}

/// <summary><c>GET /ui/{slug}/security</c>: the signed-in account's two-factor state, with enrolment or removal.</summary>
public sealed class SecurityPageEndpoint(Sessions sessions, TotpCredentials mfa) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/security");
        Name("ui_security");
        Summary("Hosted security page");
        Description("Shows whether the signed-in account has an authenticator app (TOTP) and how many recovery codes remain, with a button to set one up or a form to turn it off. Without a live tsauth_session cookie it redirects to sign-in and returns here.");
        Tags("Security");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces(StatusCodes.Status302Found);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return Results.NotFound();
        }

        return await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is { } live
            ? await SecurityFlow.PageAsync(this.HttpContext, mfa, live.User, null, cancellationToken)
            : SecurityFlow.SignIn(this.HttpContext);
    }
}

/// <summary>
/// <c>POST /ui/{slug}/security/totp</c>: starts (or restarts) authenticator enrolment and shows its QR code. Needs a
/// sign-in from the last 15 minutes.
/// </summary>
public sealed class TotpBeginEndpoint(Sessions sessions, TotpCredentials mfa, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/security/totp");
        Name("ui_security_totp_begin");
        Summary("Start authenticator enrolment");
        Description("Same-origin form post from a live session opened in the last 15 minutes (otherwise the page asks to sign in again). Shows a QR code and key for a new TOTP secret (SHA-1, 6 digits, 30 seconds) that stays pending for 10 minutes until confirmed. 409 page when an authenticator is already active.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status409Conflict, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is null)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(this.HttpContext);
        }

        if (!SecurityFlow.Recent(this.HttpContext, live.Session))
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Reauthenticate, SignInFlow.Tenant(this.HttpContext), Next: SignInFlow.SecurityPath(SignInFlow.Tenant(this.HttpContext)))
            { Account = live.User.Email }, StatusCodes.Status401Unauthorized);
        }

        if (await mfa.BeginAsync(live.User, cancellationToken) is not { } enrollment)
        {
            return await SecurityFlow.PageAsync(this.HttpContext, mfa, live.User, ("Already set up", "Two-factor authentication is already on, or your account changed. Reload and try again."), cancellationToken,
                StatusCodes.Status409Conflict);
        }

        using var qr = QRCodeGenerator.GenerateQrCode(enrollment.ProvisioningUri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qr).GetGraphic(6);
        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.TotpSetup, SignInFlow.Tenant(this.HttpContext))
        {
            Account = live.User.Email,
            Secret = enrollment.Secret,
            QrCode = "data:image/png;base64," + Convert.ToBase64String(png),
        });
    }
}

/// <summary><c>POST /ui/{slug}/security/totp/confirm</c>: activates the pending authenticator and shows recovery codes once.</summary>
public sealed class TotpConfirmEndpoint(Sessions sessions, TotpCredentials mfa, PlatformDirectory platform, AuthOptions options, ILogger<TotpConfirmEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/security/totp/confirm");
        Name("ui_security_totp_confirm");
        Summary("Turn on two-factor authentication");
        Description("Same-origin form post with code, the authenticator's current six-digit code for the pending secret, from a live session. Success activates the factor, signs the account out everywhere (every session and token), opens a fresh two-factor session for this browser, records member.mfa_enabled in the account's organizations' audit logs and shows eight single-use recovery codes once. A wrong or expired code answers 401; 10 attempts per account per 15 minutes (429).");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(this.HttpContext);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        if (await mfa.ConfirmAsync(live.User, (form.Code ?? string.Empty).Trim(), cancellationToken) is not { } issued)
        {
            return await SecurityFlow.PageAsync(this.HttpContext, mfa, live.User, ("That code is wrong or expired", "Enter the current code from your authenticator app, or start the setup again."),
                cancellationToken, StatusCodes.Status401Unauthorized);
        }

        logger.ZLogInformation($"Audit: user {live.User.Id:@UserId} turned on two-factor authentication");
        await SecurityFlow.AuditAsync(platform, tenant, live.User.Id, "member.mfa_enabled", cancellationToken);
        if (await SignInFlow.OpenSessionAsync(sessions, issued.User with { MultiFactor = true }, cancellationToken) is { } session)
        {
            BrowserSecurity.SetSessionCookie(this.HttpContext.Response, session.Token, tenant);
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.RecoveryCodes, tenant) { Account = live.User.Email, RecoveryCodes = issued.Codes });
    }
}

/// <summary><c>POST /ui/{slug}/security/totp/disable</c>: removes the authenticator after a TOTP or recovery code.</summary>
public sealed class TotpDisableEndpoint(Sessions sessions, TotpCredentials mfa, Users users, PlatformDirectory platform, AuthOptions options, ILogger<TotpDisableEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/security/totp/disable");
        Name("ui_security_totp_disable");
        Summary("Turn off two-factor authentication");
        Description("Same-origin form post with code (the authenticator's current code or an unused recovery code) from a live session; the code reauthenticates the change. Success removes the factor and its recovery codes, signs the account out everywhere, opens a fresh session for this browser and records member.mfa_disabled in the account's organizations' audit logs. Members of organizations that require two-factor authentication lose access to them until they enrol again. A wrong code answers 401; 10 attempts per account per 15 minutes (429).");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(this.HttpContext);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        if (!await mfa.DisableAsync(live.User, (form.Code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim(), cancellationToken))
        {
            return await SecurityFlow.PageAsync(this.HttpContext, mfa, live.User, ("That code is wrong or already used", "Enter the current code from your authenticator app, or an unused recovery code."),
                cancellationToken, StatusCodes.Status401Unauthorized);
        }

        logger.ZLogInformation($"Audit: user {live.User.Id:@UserId} turned off two-factor authentication");
        await SecurityFlow.AuditAsync(platform, tenant, live.User.Id, "member.mfa_disabled", cancellationToken);
        var user = await users.GetAsync(tenant, live.User.Id, cancellationToken) ?? live.User;
        if (await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is { } session)
        {
            BrowserSecurity.SetSessionCookie(this.HttpContext.Response, session.Token, tenant);
        }

        return await SecurityFlow.PageAsync(this.HttpContext, mfa, user, null, cancellationToken, notice: "Two-factor authentication is off.");
    }
}

/// <summary>Checks shared by the security pages.</summary>
internal static class SecurityFlow
{
    /// <summary>How recent a sign-in must be to start enrolment.</summary>
    public static readonly TimeSpan ReauthenticationWindow = TimeSpan.FromMinutes(15);

    /// <summary>The browser's live session and its user, or null.</summary>
    public static Task<(SessionRecord Session, UserRecord User)?> LiveAsync(HttpContext context, Sessions sessions, CancellationToken cancellationToken)
    {
        var tenant = SignInFlow.Tenant(context);
        return sessions.ResolveAsync(tenant, context.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken);
    }

    /// <summary>Whether <paramref name="session"/> was opened recently enough for a credential change.</summary>
    public static bool Recent(HttpContext context, SessionRecord session) =>
        session.CreatedAt > context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow() - ReauthenticationWindow;

    /// <summary>Sends a browser without a session to sign in, returning to the security page.</summary>
    public static IResult SignIn(HttpContext context)
    {
        var tenant = SignInFlow.Tenant(context);
        return Results.Redirect($"/ui/{tenant}/sign-in?next={Uri.EscapeDataString(SignInFlow.SecurityPath(tenant))}");
    }

    /// <summary>The security page for <paramref name="user"/>.</summary>
    public static async Task<IResult> PageAsync(HttpContext context, TotpCredentials mfa, UserRecord user, (string Title, string Message)? error, CancellationToken cancellationToken,
        int statusCode = StatusCodes.Status200OK, string? notice = null)
    {
        var (enabled, remaining) = await mfa.StatusAsync(user.InstanceId, user.Id, cancellationToken);
        return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Security, SignInFlow.Tenant(context), Error: error?.Message)
        {
            ErrorTitle = error?.Title,
            Account = user.Email,
            MfaEnabled = enabled,
            RecoveryRemaining = remaining,
            Notice = notice,
        }, statusCode);
    }

    /// <summary>Records a factor change in the organizations of a platform account; project tenants have none.</summary>
    public static Task AuditAsync(PlatformDirectory platform, string tenant, string userId, string action, CancellationToken cancellationToken) =>
        Tenancy.IsProject(tenant) ? Task.CompletedTask : platform.AuditFactorAsync(userId, action, cancellationToken);
}
