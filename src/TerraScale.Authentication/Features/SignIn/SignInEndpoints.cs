using System.Globalization;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>The fields the hosted sign-in forms post (<c>application/x-www-form-urlencoded</c>).</summary>
/// <param name="Email">The account's email address.</param>
/// <param name="Code">The six-digit emailed code (verify only).</param>
/// <param name="Next">The page to resume: an authorization request, <c>/{slug}/oauth/authorize?…</c>, or the device page.</param>
/// <param name="Password">The account password (password sign-in, sign-up and reset only).</param>
/// <param name="PendingToken">The pending sign-up's token (sign-up verification only).</param>
/// <param name="Challenge">The sealed second-factor challenge (second-factor step only).</param>
public sealed record SignInForm(string? Email, string? Code, string? Next, string? Password = null, string? PendingToken = null, string? Challenge = null);

/// <summary><c>GET /ui/{slug}/sign-in</c>: asks for the email address to send a sign-in code to.</summary>
public sealed class SignInPageEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/sign-in");
        Name("ui_sign_in");
        Summary("Hosted sign-in page");
        Description("Email passcode or password sign-in for existing accounts, with links to sign-up and password reset. next is honoured only when it is this issuer's /{slug}/oauth/authorize URL or the /ui/{slug}/device page.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(SignInFlow.WrongSlug(this.HttpContext)
            ? Results.NotFound()
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Email, SignInFlow.Tenant(this.HttpContext), Next: SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]),
                Error: this.HttpContext.Request.Query["notice"] == "credential_changed" ? CredentialLineage.ConflictNotice : null)));
}

/// <summary>
/// <c>POST /ui/{slug}/sign-in</c>: emails a code when the address has an account. The page and its timing are the same
/// either way: <see cref="SignInMail"/> delivers after the response.
/// </summary>
public sealed class RequestPasscodeEndpoint(Passcodes passcodes, SignInMail mail, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in");
        Name("ui_sign_in_request_code");
        Summary("Email a sign-in code");
        Description("Same-origin form post (the Origin header must be the public origin). The response never reveals whether the address has an account. Shared sign-in limits: 30 calls per IP per minute, 10 mail calls per IP per 15 minutes, and 5 mail calls per normalized email per hour, including unknown emails. IPv6 is grouped per /64 and additionally per /48 at four times the IP budget. Exhaustion answers 429 with Retry-After. A new code replaces the previous one.");
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
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

        var address = form.Email?.Trim() ?? string.Empty;
        if (HostedAuth.Subject(this.HttpContext.Connection.RemoteIpAddress?.ToString()) is not { } ip)
        {
            throw ApiException.Unavailable();
        }

        if (await SignInFlow.SsoAsync(this.HttpContext, address, SignInFlow.SafeNext(this.HttpContext, form.Next), cancellationToken) is { } sso)
        {
            return sso;
        }

        if ((await hosted.LimitIpAsync(ip, mails: true, cancellationToken)
            ?? await hosted.LimitCodeAsync(address, PasscodePurpose.SignIn, issuing: true, cancellationToken)) is { } wait)
        {
            return SignInFlow.Limited(this.HttpContext, SignInStep.Email, wait, address, SignInFlow.SafeNext(this.HttpContext, form.Next));
        }

        if (await passcodes.RequestAsync(SignInFlow.Tenant(this.HttpContext), address, cancellationToken) is { } issued)
        {
            mail.Enqueue(issued, AuthEmails.Locale(this.HttpContext.Request.Headers.AcceptLanguage));
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Code, SignInFlow.Tenant(this.HttpContext), address, SignInFlow.SafeNext(this.HttpContext, form.Next)));
    }
}

/// <summary>
/// <c>POST /ui/{slug}/sign-in/verify</c>: redeems the code, opens a fresh session and sets the session cookie, then
/// resumes the authorization request.
/// </summary>
public sealed class VerifyPasscodeEndpoint(Passcodes passcodes, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/verify");
        Name("ui_sign_in_verify_code");
        Summary("Sign in with an emailed code");
        Description("Same-origin form post. A code works once, for 10 minutes, and dies after 5 wrong guesses. Hosted sign-in pages share 30 calls per IP per minute and 5 attempts per normalized email per 15 minutes, including unknown emails and replaced codes; exhaustion answers 429 with Retry-After. An account-budget refusal temporarily blocks code redemption until the stored window ends and queues one best-effort lockout email notice per real account for that deadline, in the browser language; recovery remains separate. Success sets the tsauth_session cookie (Secure, HttpOnly, SameSite=Lax) and continues to next."
            + " An account with an active authenticator app gets the second-factor step (no session cookie yet); corrupt or unavailable MFA state fails closed.");
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

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (HostedAuth.Subject(this.HttpContext.Connection.RemoteIpAddress?.ToString()) is not { } ip)
        {
            throw ApiException.Unavailable();
        }

        if ((await hosted.LimitIpAsync(ip, mails: false, cancellationToken)
            ?? await hosted.LimitCodeAsync(address, PasscodePurpose.SignIn, issuing: false, cancellationToken,
                AuthEmails.Locale(this.HttpContext.Request.Headers.AcceptLanguage))) is { } wait)
        {
            AuthMetrics.SignIn("hosted_code", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Code, wait, address, next);
        }

        var user = await passcodes.RedeemAsync(SignInFlow.Tenant(this.HttpContext), address, form.Code ?? string.Empty, PasscodePurpose.SignIn, cancellationToken);
        if (user is not null && await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, user, next, cancellationToken) is { } step)
        {
            return step;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_code", "refused");
            return SignInFlow.WrongCode(this.HttpContext, SignInStep.Code, address, next);
        }

        AuthMetrics.SignIn("hosted_code", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }
}

/// <summary>
/// <c>POST /ui/{slug}/sign-in/password</c>: without a password, shows the password step for the address; with one,
/// checks it, opens a fresh session and resumes like the code sign-in.
/// </summary>
public sealed class PasswordSignInEndpoint(Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/password");
        Name("ui_sign_in_password");
        Summary("Sign in with a password");
        Description("Same-origin form post. A post without password shows the password step. With email and password it charges the password budgets: 30 calls per IP per minute and 10 attempts per normalized email per 15 minutes, including unknown emails; exhaustion answers 429 with Retry-After and queues one best-effort lockout email notice per real account. A wrong email or password and an account without a password answer the same 401 page. A right password of an account with an active authenticator app gets the second-factor step. Success sets the tsauth_session cookie and continues to next.");
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

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (await SignInFlow.SsoAsync(this.HttpContext, address, next, cancellationToken) is { } sso)
        {
            return sso;
        }

        if (string.IsNullOrEmpty(form.Password))
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Password, SignInFlow.Tenant(this.HttpContext), address, next));
        }

        var ip = SignInFlow.Ip(this.HttpContext);
        if (await hosted.LimitIpAsync(ip, mails: false, cancellationToken) is { } ipWait)
        {
            AuthMetrics.SignIn("hosted_password", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Password, ipWait, address, next);
        }

        var (user, wait) = await hosted.CheckPasswordAsync(address, form.Password, cancellationToken);
        if (wait is { } retry)
        {
            AuthMetrics.SignIn("hosted_password", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Password, retry, address, next);
        }

        if (user is not null && await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, user, next, cancellationToken) is { } step)
        {
            return step;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_password", "refused");
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Password, SignInFlow.Tenant(this.HttpContext), address, next,
                HostedTheme.Of(this.HttpContext).Passcode ? "Check both and try again, or email yourself a sign-in code." : "Check both and try again, or reset your password.")
            { ErrorTitle = "Wrong email or password" }, StatusCodes.Status401Unauthorized);
        }

        AuthMetrics.SignIn("hosted_password", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }
}

/// <summary><c>GET /ui/{slug}/sign-up</c>: asks for the new account's email address and password.</summary>
public sealed class SignUpPageEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/sign-up");
        Name("ui_sign_up");
        Summary("Hosted sign-up page");
        Description("Creates an account with an email address and a password; the account exists once the emailed code is entered. next is honoured as on the sign-in page.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(SignInFlow.WrongSlug(this.HttpContext)
            ? Results.NotFound()
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), Next: SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]))));
}

/// <summary>
/// <c>POST /ui/{slug}/sign-up</c>: starts a sign-up and asks for the emailed code. The page is
/// the same whether or not the address has an account; an existing account is mailed a sign-in code instead.
/// </summary>
public sealed class StartSignUpEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-up");
        Name("ui_sign_up_start");
        Summary("Start a sign-up");
        Description("Same-origin form post with email and password (8 to 1024 UTF-8 bytes). Shares the mail-start limits: the IP call and mail budgets, a 60-second cooldown per normalized email across every mail start, and 5 codes per normalized email per UTC clock hour; exhaustion answers 429 with Retry-After. A new address is mailed a verification code bound to a random pending token the page carries; an address with an account is mailed a sign-in code and the password is discarded.");
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
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

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (Users.NormalizeEmail(address) is null || !PasswordHasher.Acceptable(form.Password))
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), address, next,
                "Enter a valid email address and a password of at least 8 characters.")
            { ErrorTitle = "Check your details" }, StatusCodes.Status400BadRequest);
        }

        if (await SignInFlow.SsoAsync(this.HttpContext, address, next, cancellationToken) is { } sso)
        {
            return sso;
        }

        var (pending, wait) = await hosted.SignUpAsync(address, form.Password!, SignInFlow.Ip(this.HttpContext), cancellationToken);
        return wait is { } retry
            ? SignInFlow.Limited(this.HttpContext, SignInStep.SignUp, retry, address, next)
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUpCode, SignInFlow.Tenant(this.HttpContext), address, next) { PendingToken = pending });
    }
}

/// <summary>
/// <c>POST /ui/{slug}/sign-up/verify</c>: redeems the emailed code. A sign-up code with its pending token creates the
/// account; a sign-in code (the address already had one) signs that account in. Either way a session opens.
/// </summary>
public sealed class VerifySignUpEndpoint(Passcodes passcodes, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options, ILogger<VerifySignUpEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-up/verify");
        Name("ui_sign_up_verify");
        Summary("Verify a sign-up");
        Description("Same-origin form post with email, code and pending_token. Shares the sign-in code budgets: 30 calls per IP per minute and 5 attempts per normalized email per 15 minutes; exhaustion answers 429 with Retry-After. A wrong, expired or used code, or another requester's token, answers 401. Success creates the account (or signs the existing one in), sets the tsauth_session cookie and continues to next.");
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

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        var code = form.Code ?? string.Empty;
        if ((await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken)
            ?? await hosted.LimitCodeAsync(address, PasscodePurpose.SignIn, issuing: false, cancellationToken,
                AuthEmails.Locale(this.HttpContext.Request.Headers.AcceptLanguage))) is { } wait)
        {
            AuthMetrics.SignIn("hosted_sign_up", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.SignUpCode, wait, address, next, form.PendingToken);
        }

        var user = await passcodes.RedeemAsync(SignInFlow.Tenant(this.HttpContext), address, code, PasscodePurpose.SignIn, cancellationToken);
        if (user is null && form.PendingToken is { Length: > 0 } pending
            && await passcodes.RedeemSignUpAsync(SignInFlow.Tenant(this.HttpContext), address, code, pending, cancellationToken) is { } created)
        {
            logger.ZLogInformation($"Audit: hosted sign-up created user {created.Id:@UserId}");
            user = created;
        }

        if (user is not null && await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, user, next, cancellationToken) is { } step)
        {
            return step;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_sign_up", "refused");
            return SignInFlow.WrongCode(this.HttpContext, SignInStep.SignUpCode, address, next, form.PendingToken);
        }

        AuthMetrics.SignIn("hosted_sign_up", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }
}

/// <summary><c>GET /ui/{slug}/reset</c>: asks for the email address of the account whose password to reset.</summary>
public sealed class ResetPageEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/ui/{slug}/reset");
        Name("ui_password_reset");
        Summary("Hosted password reset page");
        Description("Emails a reset code, then sets a new password with it. next is honoured as on the sign-in page.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(SignInFlow.WrongSlug(this.HttpContext)
            ? Results.NotFound()
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Reset, SignInFlow.Tenant(this.HttpContext), Next: SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]))));
}

/// <summary><c>POST /ui/{slug}/reset</c>: emails a reset code when the address has an account; the page is the same either way.</summary>
public sealed class StartResetEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/reset");
        Name("ui_password_reset_start");
        Summary("Email a password reset code");
        Description("Same-origin form post with email. Shares the mail-start limits (IP call and mail budgets, the 60-second mail cooldown per normalized email, 5 codes per normalized email per UTC clock hour); exhaustion answers 429 with Retry-After. The response never reveals whether the address has an account.");
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
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

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        return await hosted.StartPasswordResetAsync(address, SignInFlow.Ip(this.HttpContext), cancellationToken) is { } wait
            ? SignInFlow.Limited(this.HttpContext, SignInStep.Reset, wait, address, next)
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetCode, SignInFlow.Tenant(this.HttpContext), address, next));
    }
}

/// <summary><c>POST /ui/{slug}/reset/complete</c>: sets a new password with the emailed reset code.</summary>
public sealed class CompleteResetEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/reset/complete");
        Name("ui_password_reset_complete");
        Summary("Set a new password with a reset code");
        Description("Same-origin form post with email, code and password. Limits: 30 calls per IP per minute and 5 attempts per normalized email per 15 minutes (429 with Retry-After); a wrong, expired or used code and an unknown address answer the same 401 page. Success signs the user out everywhere and shows a link back to sign-in.");
        Tags("Sign-in");
        // Accepts<T> documents a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
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

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (!PasswordHasher.Acceptable(form.Password))
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetCode, SignInFlow.Tenant(this.HttpContext), address, next,
                "Choose a password of at least 8 characters.")
            { ErrorTitle = "Password too short" }, StatusCodes.Status400BadRequest);
        }

        var (reset, wait) = await hosted.CompletePasswordResetAsync(address, form.Code ?? string.Empty, form.Password!, SignInFlow.Ip(this.HttpContext), cancellationToken);
        return wait is { } retry ? SignInFlow.Limited(this.HttpContext, SignInStep.ResetCode, retry, address, next)
            : reset ? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetDone, SignInFlow.Tenant(this.HttpContext), address, next))
            : SignInFlow.WrongCode(this.HttpContext, SignInStep.ResetCode, address, next);
    }
}

/// <summary>Checks shared by the sign-in forms.</summary>
internal static class SignInFlow
{
    public static IResult Limited(HttpContext context, SignInStep step, TimeSpan wait, string email, string? next, string? pendingToken = null, string? challenge = null)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
        var retryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.RetryAfter = retryAfter;
        context.Response.Headers.CacheControl = "no-store";
        return SignInPageModel.Render(context,
            new SignInPageModel(step, Tenant(context), email, next, $"Try again in {retryAfter} seconds.")
            {
                ErrorTitle = "Too many attempts", Limited = true, PendingToken = pendingToken, Challenge = challenge,
            },
            StatusCodes.Status429TooManyRequests);
    }

    public static IResult WrongCode(HttpContext context, SignInStep step, string email, string? next, string? pendingToken = null) =>
        SignInPageModel.Render(context, new SignInPageModel(step, Tenant(context), email, next, "This code is invalid, expired, already used, or superseded by a concurrent request. Check the latest email, or request a new code.")
        {
            ErrorTitle = "That code is wrong or expired",
            PendingToken = pendingToken,
        }, StatusCodes.Status401Unauthorized);

    /// <summary>The browser's rate-limit subject; a request without a peer address cannot be limited, so it is refused.</summary>
    public static string Ip(HttpContext context) =>
        HostedAuth.Subject(context.Connection.RemoteIpAddress?.ToString()) ?? throw ApiException.Unavailable();

    /// <summary>
    /// The second-factor step for a user whose primary credential was just verified and who has an active authenticator
    /// app, or null to open a session directly (which still refuses an active factor or stale authentication).
    /// </summary>
    public static async Task<IResult?> SecondFactorAsync(HttpContext context, TotpCredentials mfa, UserRecord user, string? next, CancellationToken cancellationToken)
    {
        // The user is read after the factor, so an activation or sign-out everywhere meanwhile yields no challenge.
        if (!(await mfa.StatusAsync(user.InstanceId, user.Id, cancellationToken)).Enabled
            || await mfa.ChallengeAsync(user, cancellationToken) is not { } challenge)
        {
            return null;
        }

        AuthMetrics.SignIn("hosted_mfa", "challenged");
        return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Mfa, Tenant(context), user.Email, next) { Challenge = challenge });
    }

    /// <summary>A primary-only session for <paramref name="user"/>, or null when MFA or a newer epoch refuses one.</summary>
    public static async Task<IssuedSession?> OpenSessionAsync(Sessions sessions, UserRecord user, CancellationToken cancellationToken)
    {
        try
        {
            return await sessions.CreateAsync(user, cancellationToken);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status401Unauthorized && error.Code == "invalid_credentials")
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the browser's session with <paramref name="session"/> and resumes <paramref name="next"/>. Unless it came
    /// <paramref name="viaSso"/>, a platform account whose domain signs in through its organization's identity provider
    /// is refused: the new session ends at once and the page points at single sign-on.
    /// </summary>
    public static async Task<IResult> SignedInAsync(HttpContext context, Sessions sessions, IssuedSession session, string? next, CancellationToken cancellationToken, bool viaSso = false)
    {
        var tenant = Tenant(context);
        if (!viaSso && !Tenancy.IsProject(tenant)
            && await context.RequestServices.GetRequiredService<Users>().GetAsync(tenant, session.Session.UserId, cancellationToken) is { } user
            && await SsoAsync(context, user.Email, next, cancellationToken) is { } sso)
        {
            await sessions.RevokeAsync(tenant, session.Token, cancellationToken);
            return sso;
        }

        // The browser's previous session, if any, ends with this sign-in.
        await sessions.RevokeAsync(tenant, context.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken);
        BrowserSecurity.SetSessionCookie(context.Response, session.Token, tenant);
        return SignInPageModel.Render(context, new SignInPageModel(next is null ? SignInStep.SignedIn : SignInStep.Continue, tenant, Next: next));
    }

    /// <summary>The single sign-on page when <paramref name="email"/> must sign in through its organization's identity provider; otherwise null.</summary>
    public static async Task<IResult?> SsoAsync(HttpContext context, string email, string? next, CancellationToken cancellationToken) =>
        !Tenancy.IsProject(Tenant(context)) && await context.RequestServices.GetRequiredService<SsoSignIn>().RequiredAsync(email, cancellationToken)
            ? SignInPageModel.Render(context, new SignInPageModel(SignInStep.Sso, Tenant(context), email, next) { SsoUrl = SsoSignIn.StartUrl(email, next) })
            : null;

    /// <summary>Whether the page's slug is not a tenant: <c>terrascale</c> or an existing project (<see cref="Tenancy"/>).</summary>
    public static bool WrongSlug(HttpContext context) => Tenancy.Resolved(context) is not { } tenant
        || !string.Equals(context.GetRouteValue("slug") as string, tenant, StringComparison.Ordinal)
        || !Allowed(context);

    // A sign-in method the tenant turned off has no pages: passcode sign-in, password sign-in and reset, or sign-up.
    private static bool Allowed(HttpContext context)
    {
        var theme = HostedTheme.Of(context);
        var page = (context.Request.Path.Value ?? string.Empty).Split('/', 4) is [_, _, _, var rest] ? rest.TrimEnd('/') : string.Empty;
        return page switch
        {
            "sign-in" => theme.Passcode || HttpMethods.IsGet(context.Request.Method),
            "sign-in/verify" => theme.Passcode,
            "sign-in/password" or "reset" or "reset/complete" => theme.Password,
            "sign-up" or "sign-up/verify" => theme.SignUp,
            _ => true,
        };
    }

    /// <summary>The tenant of a hosted page whose slug <see cref="WrongSlug"/> accepted.</summary>
    public static string Tenant(HttpContext context) => Tenancy.Resolved(context) ?? throw new InvalidOperationException("The page has no tenant.");

    /// <summary>The device page, which a sign-in may resume.</summary>
    public static string DevicePath => $"/ui/{OidcIssuer.Slug}/device";

    /// <summary>The tenant's security page, which a sign-in may resume.</summary>
    public static string SecurityPath(string tenant) => $"/ui/{tenant}/security";

    /// <summary>
    /// Only the page's own issuer's authorization endpoint or security page, or for the platform its device page, may be resumed, as a
    /// same-origin path.
    /// </summary>
    public static string? SafeNext(HttpContext context, string? next) =>
        next is not null && Tenancy.Resolved(context) is { } tenant
            && (next.StartsWith($"/{tenant}/oauth/authorize?", StringComparison.Ordinal)
                || next == SecurityPath(tenant)
                || !Tenancy.IsProject(tenant) && (next == DevicePath || next.StartsWith(DevicePath + "?", StringComparison.Ordinal)))
            && next.Length <= 4096
            && !next.Any(character => character is '\\' or '#' || char.IsControl(character) || char.IsWhiteSpace(character))
            ? next
            : null;

    /// <summary>
    /// The posted form, or <see langword="null"/> for a wrong slug, a cross-origin post or a body that is not a form.
    /// Login CSRF is refused by requiring the browser's Origin header to be the public origin.
    /// </summary>
    public static async Task<SignInForm?> ReadAsync(HttpContext context, AuthOptions options, CancellationToken cancellationToken)
    {
        if (WrongSlug(context) || !context.Request.HasFormContentType
            || context.Request.Headers.Origin is not [{ } origin] || !IsPublicOrigin(origin, options.PublicOrigin))
        {
            return null;
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        return new SignInForm(form["email"], form["code"], form["next"], form["password"], form["pending_token"], form["challenge"]);
    }

    /// <summary>
    /// Whether a serialized Origin (RFC 6454) names the public origin: same scheme, same host compared as the ASCII
    /// (punycode) form browsers send, and same effective port, so <c>:443</c> or letter case in the configuration
    /// never refuses a legitimate post. <c>null</c>, paths and credentials never match.
    /// </summary>
    public static bool IsPublicOrigin(string origin, Uri publicOrigin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
        && origin.Count(character => character == '/') == 2
        && string.IsNullOrEmpty(parsed.UserInfo) && string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment)
        && parsed.Scheme == publicOrigin.Scheme
        && string.Equals(parsed.IdnHost, publicOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && parsed.Port == publicOrigin.Port;

    public static IResult Refused(HttpContext context) => WrongSlug(context)
        ? Results.NotFound()
        : SignInPageModel.Render(context, new SignInPageModel(SignInStep.Rejected, Tenant(context)), StatusCodes.Status403Forbidden);
}
