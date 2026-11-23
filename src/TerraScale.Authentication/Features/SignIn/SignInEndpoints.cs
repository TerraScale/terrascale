using System.Globalization;
using TerraScale.Authentication.Features.AttackProtection;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

public sealed record SignInForm(string? Email, string? Code, string? Next, string? Password = null, string? PendingToken = null, string? Challenge = null, string? Username = null);

public sealed class SignInPageEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/ui/{slug}/sign-in");
        Name("ui_sign_in");
        Summary("Hosted sign-in page");
        Description("Email passcode or password sign-in for existing accounts, with links to sign-up and password reset. next is honoured only when it is this issuer's /{slug}/oauth/authorize URL, the /ui/{slug}/device page, a project's /ui/{slug}/agent-enrollments page with or without user_code, or exactly the dashboard's https://dash.terrascale.tech/profile or https://dash.terrascale.tech/profile?tab=security URL.");
        Tags("Sign-in");
        Produces<string>(StatusCodes.Status200OK, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return TypedResults.NotFound();
        }

        var next = SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]);
        return await SignInFlow.ResumeAsync(this.HttpContext, next, cancellationToken)
            ?? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Email, SignInFlow.Tenant(this.HttpContext), Next: next,
                Error: this.HttpContext.Request.Query["notice"] == "credential_changed" ? SignInFlow.Copy(this.HttpContext).NoticeCredentialChanged : null));
    }
}

public sealed class RequestPasscodeEndpoint(SignInMail mail, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in");
        Metadata(new BotChallengeForm("sign_in", SignInStep.Email));
        Name("ui_sign_in_request_code");
        Summary("Email a sign-in code");
        Description("Same-origin form post (the Origin header must be the public origin). The response never reveals whether the address has an account. Shared mail-start limits: an IP call bucket of 120 refilling 30 per minute, an IP mail bucket of 30 refilling 60 per hour, a 60-second cooldown per normalized email across sign-in, sign-up and reset, and 5 mail calls per normalized email per hour, including unknown emails. IPv6 is grouped per /64 and additionally per /48 at four times the IP budget. Exhaustion answers 429 with Retry-After; a spent hourly code budget is told plainly - no new code was sent, when it resets, the resend control disabled for the same wait - identically for every address, whether or not it has an account, so nothing becomes enumerable. A new code replaces the previous one. A full or stopping delivery queue answers 503 before code issuance, including for unknown addresses.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var tenant = SignInFlow.Tenant(this.HttpContext);
        var pendingToken = PendingSocialLinks.Read(this.HttpContext, tenant);
        var pendingIdentity = PendingSocialLinks.Open(options, tenant, pendingToken, this.HttpContext.RequestServices.GetRequiredService<TimeProvider>());
        pendingIdentity = pendingIdentity?.Email == Users.NormalizeEmail(form.Email ?? string.Empty) ? pendingIdentity : null;
        var address = pendingIdentity?.Email ?? form.Email?.Trim() ?? string.Empty;
        if (HostedAuth.Subject(this.HttpContext.Connection.RemoteIpAddress?.ToString()) is not { } ip)
        {
            throw ApiException.Unavailable();
        }

        if (await SignInFlow.SsoAsync(this.HttpContext, address, SignInFlow.SafeNext(this.HttpContext, form.Next), cancellationToken) is { } sso)
        {
            return sso;
        }

        using var reservation = mail.Reserve();
        if (await hosted.LimitMailAsync(address, ip, cancellationToken) is { } refusal)
        {
            return refusal.BudgetExhausted
                ? SignInFlow.BudgetExhausted(this.HttpContext, SignInStep.Code, refusal.Wait, address, SignInFlow.SafeNext(this.HttpContext, form.Next))
                : SignInFlow.Limited(this.HttpContext, SignInStep.Code, refusal.Wait, address, SignInFlow.SafeNext(this.HttpContext, form.Next));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pendingIdentity is not null)
        {
            await reservation.EnqueueSocialAsync(tenant, address, pendingToken!, SocialProviders.Label(pendingIdentity.Identity.Provider), AuthEmails.Locale(this.HttpContext.Request));
        }
        else
        {
            PendingSocialLinks.Delete(this.HttpContext, tenant);
            await reservation.EnqueueCodeAsync(tenant, address, PasscodePurpose.SignIn, AuthEmails.Locale(this.HttpContext.Request));
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Code, SignInFlow.Tenant(this.HttpContext), address, SignInFlow.SafeNext(this.HttpContext, form.Next)));
    }
}

public sealed class VerifyPasscodeEndpoint(Passcodes passcodes, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/verify");
        Name("ui_sign_in_verify_code");
        Summary("Sign in with an emailed code");
        Description("Same-origin form post. A code works once, for 30 minutes (ten minutes for magic links and pending social links), until a newer code replaces it, and dies after 5 wrong guesses. Hosted sign-in pages share the IP call bucket of 120 refilling 30 per minute and 5 attempts per normalized email per 30 minutes, including unknown emails and replaced codes; exhaustion answers 429 with Retry-After. An account-budget refusal temporarily blocks code redemption until the stored window ends and queues one best-effort lockout email notice per real account for that deadline, in the browser language; recovery remains separate. Success sets the tsauth_session cookie (Secure, HttpOnly, SameSite=Lax) and continues to next."
            + " An account with an active authenticator app gets the second-factor step (no session cookie yet); corrupt or unavailable MFA state fails closed.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var magic = MagicLinks.Open(options, SignInFlow.Tenant(this.HttpContext), form.Challenge,
            this.HttpContext.RequestServices.GetRequiredService<TimeProvider>());
        var tenant = SignInFlow.Tenant(this.HttpContext);
        var pendingSocial = PendingSocialLinks.Read(this.HttpContext, tenant);
        var pendingIdentity = PendingSocialLinks.Open(options, tenant, pendingSocial, this.HttpContext.RequestServices.GetRequiredService<TimeProvider>());
        pendingIdentity = pendingIdentity?.Email == Users.NormalizeEmail(form.Email ?? string.Empty) ? pendingIdentity : null;
        var address = pendingIdentity?.Email ?? magic?.Email ?? form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (HostedAuth.Subject(this.HttpContext.Connection.RemoteIpAddress?.ToString()) is not { } ip)
        {
            throw ApiException.Unavailable();
        }

        if ((await hosted.LimitIpAsync(ip, mails: false, cancellationToken)
            ?? await hosted.LimitCodeAsync(address, PasscodePurpose.SignIn, cancellationToken,
                AuthEmails.Locale(this.HttpContext.Request))) is { } wait)
        {
            AuthMetrics.SignIn("hosted_code", "rate_limited");
            return magic is null
                ? SignInFlow.Limited(this.HttpContext, SignInStep.Code, wait, address, next)
                : MagicLinks.CodePage(this.HttpContext, magic, form.Challenge!, "Too many attempts. Try again later.", StatusCodes.Status429TooManyRequests);
        }

        var user = pendingIdentity is not null
            ? await passcodes.RedeemSocialAsync(tenant, pendingSocial!, form.Code ?? string.Empty, cancellationToken)
            : await passcodes.RedeemAsync(tenant, address, form.Code ?? string.Empty, PasscodePurpose.SignIn, cancellationToken: cancellationToken);
        if (user is not null && pendingIdentity is not null)
        {
            PendingSocialLinks.Delete(this.HttpContext, tenant);
        }
        if (user is not null && await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, user with { InboxProven = true }, next, cancellationToken) is { } step)
        {
            return step;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_code", "refused");
            return magic is null
                ? SignInFlow.WrongCode(this.HttpContext, SignInStep.Code, address, next)
                : MagicLinks.CodePage(this.HttpContext, magic, form.Challenge!, "This code is invalid or expired.", StatusCodes.Status401Unauthorized);
        }

        AuthMetrics.SignIn("hosted_code", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }
}

public sealed class PasswordSignInEndpoint(Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/password");
        Metadata(new BotChallengeForm("sign_in", SignInStep.Password, PasswordPrompt: true));
        Name("ui_sign_in_password");
        Summary("Sign in with a password");
        Description("Same-origin form post. A post without password shows the password step. With email and password it charges the password budgets: 10 attempts per normalized email per 15 minutes; with account lockout on, the account locks after 10 failures for 30 minutes. A wrong email or password and an account without a password answer the same 401 page. A right password of an account with an active authenticator app gets the second-factor step. Success sets the tsauth_session cookie and continues to next.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

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

        var (user, wait, reservation) = await hosted.CheckPasswordAsync(address, form.Password, cancellationToken,
            AuthEmails.Locale(this.HttpContext.Request));
        if (wait is { } retry)
        {
            AuthMetrics.SignIn("hosted_password", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.Password, retry, address, next);
        }

        if (user is not null && reservation is not null && !await sessions.CanStartSignInAsync(user, cancellationToken))
        {
            user = null;
        }

        if (user is not null && await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, user, next, cancellationToken, reservation) is { } step)
        {
            return step;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_password", "refused");
            var copy = SignInFlow.Copy(this.HttpContext);
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Password, SignInFlow.Tenant(this.HttpContext), address, next,
                HostedTheme.Of(this.HttpContext).Passcode ? copy.ErrorWrongCredentialsPasscode : copy.ErrorWrongCredentialsReset)
            { ErrorTitle = copy.ErrorWrongCredentialsTitle }, StatusCodes.Status401Unauthorized);
        }

        AuthMetrics.SignIn("hosted_password", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken, reservation: reservation);
    }
}

public sealed class SignUpPageEndpoint : EndpointWithoutRequest<IResult>
{
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

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return TypedResults.NotFound();
        }

        var next = SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]);
        return await SignInFlow.ResumeAsync(this.HttpContext, next, cancellationToken)
            ?? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), Next: next));
    }
}

public sealed class StartSignUpEndpoint(HostedAuth hosted, AuthOptions options, SignUpRestrictions restrictions, Users users) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-up");
        Metadata(new BotChallengeForm("sign_up", SignInStep.SignUp));
        Name("ui_sign_up_start");
        Summary("Start a sign-up");
        Description("Same-origin form post with email and password (10 to 256 Unicode characters, not a common, repetitive or own-email password). One IP may start 20 sign-ups per hour (429 with Retry-After). Shares the mail-start limits: the IP call and mail budgets, a 60-second cooldown per normalized email across every mail start, and 5 codes per normalized email per UTC clock hour; exhaustion answers 429 with Retry-After. A new address is mailed a verification code bound to a random pending token the page carries; an address with an account is mailed a sign-in code and the password is discarded. An address whose account or pending sign-up already spent the hour's code budget is told that plainly (429, Retry-After, resend disabled) - identically for every refused address, whether or not it has an account, so nothing becomes enumerable. A full or stopping delivery queue answers 503 before code issuance, including for unknown addresses.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        var copy = SignInFlow.Copy(this.HttpContext);
        if (Users.NormalizeEmail(address) is null)
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), address, next,
                copy.ErrorEnterValidEmail)
            { ErrorTitle = copy.ErrorCheckDetailsTitle }, StatusCodes.Status400BadRequest);
        }

        if (await this.HttpContext.RequestServices.GetRequiredService<PasswordProtection>().CheckAsync(
            SignInFlow.Tenant(this.HttpContext), form.Password, address, cancellationToken) is not PasswordProblem.None and var problem)
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), address, next,
                SignInFlow.PasswordError(copy, problem))
            { ErrorTitle = copy.ErrorChoosePasswordTitle }, StatusCodes.Status400BadRequest);
        }

        if (await SignInFlow.SsoAsync(this.HttpContext, address, next, cancellationToken) is { } sso)
        {
            return sso;
        }

        if (await restrictions.AdmissionAsync(SignInFlow.Tenant(this.HttpContext), address, false, cancellationToken) is null)
        {
            if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), false, cancellationToken) is { } wait)
            {
                return HostedAuth.TooMany(wait);
            }

            var joined = await restrictions.JoinAsync(SignInFlow.Tenant(this.HttpContext), address, cancellationToken);
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), address, next,
                joined ? null : "Sign-up is unavailable for this address.")
            {
                Notice = joined ? "Your request has been received." : null,
            }, joined ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden);
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } usernameWait)
        {
            return SignInFlow.Limited(this.HttpContext, SignInStep.SignUp, usernameWait, address, next);
        }

        var username = HostedTheme.Of(this.HttpContext).Username ? Users.NormalizeUsername(form.Username) : null;
        if (HostedTheme.Of(this.HttpContext).Username && username is null)
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUp, SignInFlow.Tenant(this.HttpContext), address, next,
                Error: "Use a username of 3 to 32 letters, digits, underscores or dots."), StatusCodes.Status400BadRequest);
        }

        if (username is not null && await users.FindByUsernameAsync(SignInFlow.Tenant(this.HttpContext), username, cancellationToken) is not null)
        {
            return SignInFlow.UsernameTaken(this.HttpContext, address, next);
        }

        var (pending, refusal) = await hosted.SignUpAsync(address, form.Password!, SignInFlow.Ip(this.HttpContext),
            AuthEmails.Locale(this.HttpContext.Request), username, cancellationToken);
        return refusal is { } limited
            ? limited.BudgetExhausted
                ? SignInFlow.BudgetExhausted(this.HttpContext, SignInStep.SignUpCode, limited.Wait, address, next)
                : SignInFlow.Limited(this.HttpContext, SignInStep.SignUp, limited.Wait, address, next)
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUpCode, SignInFlow.Tenant(this.HttpContext), address, next) { PendingToken = pending });
    }
}

public sealed class ResendSignUpEndpoint(SignInMail mail, HostedAuth hosted, AuthOptions options, SignUpRestrictions restrictions) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-up/resend");
        Metadata(new BotChallengeForm("sign_up", SignInStep.SignUpCode));
        Name("ui_sign_up_resend");
        Summary("Resend the sign-up code");
        Description("Same-origin form post with the email and pending token of the pending sign-up. A fresh code is mailed to the same pending sign-up, replacing the previous code; an address that got an account meanwhile is mailed a sign-in code instead. The page is the same either way. Shares the mail-start limits (IP call and mail budgets, the 60-second mail cooldown per normalized email, 5 codes per normalized email per UTC clock hour); exhaustion answers 429 with Retry-After, and a spent hourly code budget is told plainly with its resend control disabled - identically for every refused address, whether or not it has an account, so nothing becomes enumerable. A full or stopping delivery queue answers 503 before code issuance, including for unknown addresses.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (await restrictions.AdmissionAsync(SignInFlow.Tenant(this.HttpContext), address, false, cancellationToken) is null)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        using var reservation = mail.Reserve();
        if (await hosted.LimitMailAsync(address, SignInFlow.Ip(this.HttpContext), cancellationToken) is { } refusal)
        {
            return refusal.BudgetExhausted
                ? SignInFlow.BudgetExhausted(this.HttpContext, SignInStep.SignUpCode, refusal.Wait, address, next, form.PendingToken)
                : SignInFlow.Limited(this.HttpContext, SignInStep.SignUpCode, refusal.Wait, address, next, form.PendingToken);
        }

        await reservation.EnqueueSignUpResendAsync(SignInFlow.Tenant(this.HttpContext), address,
            AuthEmails.Locale(this.HttpContext.Request));
        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUpCode, SignInFlow.Tenant(this.HttpContext), address, next)
        {
            PendingToken = form.PendingToken,
            Notice = SignInFlow.Copy(this.HttpContext).NoticeResendSent.Replace("{email}", address),
        });
    }
}

public sealed class VerifySignUpEndpoint(Passcodes passcodes, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options, ILogger<VerifySignUpEndpoint> logger) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-up/verify");
        Name("ui_sign_up_verify");
        Summary("Verify a sign-up");
        Description("Same-origin form post with email, code and pending_token. Shares the sign-in code budgets: the IP call bucket of 120 refilling 30 per minute and 5 attempts per normalized email per 30 minutes; exhaustion answers 429 with Retry-After. A wrong, expired or used code, or another requester's token, answers 401. Success creates the account (or signs the existing one in), sets the tsauth_session cookie and continues to next.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

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
            ?? await hosted.LimitCodeAsync(address, PasscodePurpose.SignIn, cancellationToken,
                AuthEmails.Locale(this.HttpContext.Request))) is { } wait)
        {
            AuthMetrics.SignIn("hosted_sign_up", "rate_limited");
            return SignInFlow.Limited(this.HttpContext, SignInStep.SignUpCode, wait, address, next, form.PendingToken);
        }

        var user = await passcodes.RedeemAsync(SignInFlow.Tenant(this.HttpContext), address, code, PasscodePurpose.SignIn, cancellationToken: cancellationToken);
        if (user is null && form.PendingToken is { Length: > 0 } pending
            && await passcodes.RedeemSignUpAsync(SignInFlow.Tenant(this.HttpContext), address, code, pending, cancellationToken) is { } created)
        {
            logger.ZLogInformation($"Audit: hosted sign-up created user {created.Id:@UserId}");
            await this.HttpContext.RequestServices.GetRequiredService<AccountProtection>()
                .SignedInAsync(this.HttpContext, created.InstanceId, created.Id, cancellationToken, notify: false);
            user = created;
        }

        if (user is not null && await SignInFlow.SecondFactorAsync(this.HttpContext, mfa, user with { InboxProven = true }, next, cancellationToken) is { } step)
        {
            return step;
        }

        if (user is null || await SignInFlow.OpenSessionAsync(sessions, user, cancellationToken) is not { } session)
        {
            AuthMetrics.SignIn("hosted_sign_up", "refused");
            return form.PendingToken is { Length: > 0 and <= 256 } token
                && await passcodes.PendingUsernameTakenAsync(SignInFlow.Tenant(this.HttpContext), address, token, cancellationToken)
                ? SignInFlow.UsernameTaken(this.HttpContext, address, next)
                : SignInFlow.WrongCode(this.HttpContext, SignInStep.SignUpCode, address, next, form.PendingToken);
        }

        AuthMetrics.SignIn("hosted_sign_up", "succeeded");
        return await SignInFlow.SignedInAsync(this.HttpContext, sessions, session, next, cancellationToken);
    }
}

public sealed class ResetPageEndpoint : EndpointWithoutRequest<IResult>
{
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

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(SignInFlow.WrongSlug(this.HttpContext)
            ? TypedResults.NotFound()
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.Reset, SignInFlow.Tenant(this.HttpContext), Next: SignInFlow.SafeNext(this.HttpContext, this.HttpContext.Request.Query["next"]))));
}

public sealed class StartResetEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/reset");
        Metadata(new BotChallengeForm("reset", SignInStep.Reset));
        Name("ui_password_reset_start");
        Summary("Email a password reset code");
        Description("Same-origin form post with email. Shares the mail-start limits (IP call and mail budgets, the 60-second mail cooldown per normalized email, 5 codes per normalized email per UTC clock hour); exhaustion answers 429 with Retry-After. The response never reveals whether the address has an account; a spent hourly code budget is told plainly (429, Retry-After, resend disabled), identically for every refused address, so nothing becomes enumerable. A full or stopping delivery queue answers 503 before code issuance, including for unknown addresses.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (await hosted.StartPasswordResetAsync(address, SignInFlow.Ip(this.HttpContext),
            AuthEmails.Locale(this.HttpContext.Request), cancellationToken) is { } refusal)
        {
            return refusal.BudgetExhausted
                ? SignInFlow.BudgetExhausted(this.HttpContext, SignInStep.ResetCode, refusal.Wait, address, next)
                : SignInFlow.Limited(this.HttpContext, SignInStep.Reset, refusal.Wait, address, next);
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetCode, SignInFlow.Tenant(this.HttpContext), address, next));
    }
}

public sealed class ResendResetEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/reset/resend");
        Metadata(new BotChallengeForm("reset", SignInStep.ResetCode));
        Name("ui_password_reset_resend");
        Summary("Resend the password reset code");
        Description("Same-origin form post with the email of the reset code step. A fresh reset code is mailed to the same address, replacing the previous one, and the visitor stays on the code step. The response never reveals whether the address has an account; a spent hourly code budget is told plainly (429, Retry-After, resend disabled), identically for every refused address, so nothing becomes enumerable. Shares the mail-start limits (IP call and mail budgets, the 60-second mail cooldown per normalized email, 5 codes per normalized email per UTC clock hour). A full or stopping delivery queue answers 503 before code issuance, including for unknown addresses.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        if (await hosted.StartPasswordResetAsync(address, SignInFlow.Ip(this.HttpContext),
            AuthEmails.Locale(this.HttpContext.Request), cancellationToken) is { } refusal)
        {
            return refusal.BudgetExhausted
                ? SignInFlow.BudgetExhausted(this.HttpContext, SignInStep.ResetCode, refusal.Wait, address, next)
                : SignInFlow.Limited(this.HttpContext, SignInStep.ResetCode, refusal.Wait, address, next);
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetCode, SignInFlow.Tenant(this.HttpContext), address, next)
        {
            Notice = SignInFlow.Copy(this.HttpContext).NoticeResetSent.Replace("{email}", address),
        });
    }
}

public sealed class CompleteResetEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/reset/complete");
        Metadata(new BotChallengeForm("reset", SignInStep.ResetCode));
        Name("ui_password_reset_complete");
        Summary("Set a new password with a reset code");
        Description("Same-origin form post with email, code and password; the password follows the sign-up policy (400 outside it, the code stays live). Limits: the IP call bucket of 120 refilling 30 per minute and 5 attempts per normalized email per 30 minutes (429 with Retry-After); a wrong, expired or used code and an unknown address answer the same 401 page. Success signs the user out everywhere and continues to the sign-in page.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var address = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        var copy = SignInFlow.Copy(this.HttpContext);
        if (await this.HttpContext.RequestServices.GetRequiredService<PasswordProtection>().CheckAsync(
            SignInFlow.Tenant(this.HttpContext), form.Password, address, cancellationToken) is not PasswordProblem.None and var problem)
        {
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetCode, SignInFlow.Tenant(this.HttpContext), address, next,
                SignInFlow.PasswordError(copy, problem))
            { ErrorTitle = copy.ErrorChoosePasswordTitle }, StatusCodes.Status400BadRequest);
        }

        var (reset, wait) = await hosted.CompletePasswordResetAsync(address, form.Code ?? string.Empty, form.Password!, SignInFlow.Ip(this.HttpContext),
            AuthEmails.Locale(this.HttpContext.Request), cancellationToken);
        return wait is { } retry ? SignInFlow.Limited(this.HttpContext, SignInStep.ResetCode, retry, address, next)
            : reset ? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.ResetDone, SignInFlow.Tenant(this.HttpContext), address, next))
            : SignInFlow.WrongCode(this.HttpContext, SignInStep.ResetCode, address, next);
    }
}

internal static class SignInFlow
{
    public static IResult Limited(HttpContext context, SignInStep step, TimeSpan wait, string email, string? next, string? pendingToken = null, string? challenge = null)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
        var copy = Copy(context);
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return SignInPageModel.Render(context,
            new SignInPageModel(step, Tenant(context), email, next, copy.ErrorTryAgainIn.Replace("{wait}", RetryWait(copy, seconds)))
            {
                ErrorTitle = copy.ErrorTooManyTitle,
                Limited = true,
                PendingToken = pendingToken,
                Challenge = challenge,
            },
            StatusCodes.Status429TooManyRequests);
    }

    public static IResult BudgetExhausted(HttpContext context, SignInStep step, TimeSpan wait, string email, string? next, string? pendingToken = null, string? challenge = null)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
        var copy = Copy(context);
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return SignInPageModel.Render(context,
            new SignInPageModel(step, Tenant(context), email, next, copy.ErrorBudgetExhausted.Replace("{wait}", RetryWait(copy, seconds)))
            {
                ErrorTitle = copy.ErrorTooManyCodesTitle,
                CodeBudgetExhausted = true,
                WaitSeconds = seconds,
                PendingToken = pendingToken,
                Challenge = challenge,
            },
            StatusCodes.Status429TooManyRequests);
    }

    public static string RetryWait(HostedPageCopy copy, long seconds) => seconds > 120
        ? copy.RetryWaitMinutes.Replace("{minutes}", ((long)Math.Ceiling(seconds / 60.0)).ToString(CultureInfo.InvariantCulture))
        : copy.RetryWaitSeconds.Replace("{seconds}", seconds.ToString(CultureInfo.InvariantCulture));

    public static string RetryWait(long seconds) => RetryWait(HostedCopies.English, seconds);

    public static HostedPageCopy Copy(HttpContext context) => HostedCopies.For(AuthEmails.Locale(context.Request));

    public static string PasswordError(HostedPageCopy copy, PasswordProblem problem) => problem switch
    {
        PasswordProblem.Breached => PasswordPolicy.Message(problem),
        PasswordProblem.Length => copy.ErrorPasswordLength,
        _ => copy.ErrorPasswordCommon,
    };

    public static IResult WrongCode(HttpContext context, SignInStep step, string email, string? next, string? pendingToken = null)
    {
        var copy = Copy(context);
        return SignInPageModel.Render(context, new SignInPageModel(step, Tenant(context), email, next, copy.ErrorWrongCode)
        {
            ErrorTitle = copy.ErrorWrongCodeTitle,
            PendingToken = pendingToken,
        }, StatusCodes.Status401Unauthorized);
    }

    public static IResult Transient(HttpContext context)
    {
        var form = context.Request.HasFormContentType ? context.Request.Form : null;
        var address = (form?["email"].ToString() ?? string.Empty).Trim();
        var email = Users.NormalizeEmail(address) is { } normalized ? normalized : address;
        var next = SafeNext(context, form?["next"].ToString());
        var path = context.Request.Path.Value ?? string.Empty;
        var pending = form?["pending_token"].ToString();
        SignInStep step;
        if (path.Contains("/reset", StringComparison.Ordinal))
        {
            step = email.Length == 0 ? SignInStep.Reset : SignInStep.ResetCode;
        }
        else if (path.Contains("sign-up", StringComparison.Ordinal))
        {
            step = string.IsNullOrEmpty(pending) ? SignInStep.SignUp : SignInStep.SignUpCode;
        }
        else if (path.Contains("password", StringComparison.Ordinal) && email.Length > 0)
        {
            step = SignInStep.Password;
        }
        else if (email.Length > 0)
        {
            step = SignInStep.Code;
        }
        else
        {
            step = SignInStep.Email;
            email = string.Empty;
        }

        var copy = Copy(context);
        return SignInPageModel.Render(context, new SignInPageModel(step, Tenant(context), email, next, copy.ErrorTransient)
        {
            ErrorTitle = copy.ErrorTransientTitle,
            PendingToken = string.IsNullOrEmpty(pending) ? null : pending,
        }, StatusCodes.Status503ServiceUnavailable);
    }

    public static string Ip(HttpContext context) =>
        HostedAuth.Subject(context.Connection.RemoteIpAddress?.ToString()) ?? throw ApiException.Unavailable();

    public static async Task<IResult?> SecondFactorAsync(HttpContext context, TotpCredentials mfa, UserRecord user, string? next, CancellationToken cancellationToken, AttemptLimiter.Reservation? reservation = null, bool forceEnrolled = false)
    {
        var policy = Tenancy.IsProject(user.InstanceId)
            ? (await context.RequestServices.GetRequiredService<ProjectAuthSettings>().GetAsync(user.InstanceId, cancellationToken)).Configuration ?? ConfigurationBody.Default
            : ConfigurationBody.Default;
        var sso = context.RequestServices.GetRequiredService<SsoSignIn>();
        var ssoProof = Tenancy.IsProject(user.InstanceId) ? null : sso.FactorProofFor(context, user);
        if (forceEnrolled && ssoProof is null)
        {
            throw new InvalidOperationException("The SSO factor challenge requires its account proof.");
        }

        var ssoRequired = ssoProof is not null && await sso.RequiresMfaAsync(context, user, cancellationToken);
        if (ssoRequired)
        {
            user = user with { InboxProven = true };
        }

        var enrolled = await mfa.HasAllowedFactorAsync(user, ssoProof is not null, cancellationToken);
        if (!policy.RequireMfa && !ssoRequired && !(forceEnrolled && enrolled)
            && (!policy.AllowTotp || !(await mfa.StatusAsync(user.InstanceId, user.Id, cancellationToken)).Enabled))
        {
            return null;
        }

        if (!enrolled && !user.InboxProven)
        {
            return await VerifyEnrollmentInboxEndpoint.SendAsync(context, user, next, cancellationToken);
        }

        if (await mfa.ChallengeAsync(user, !enrolled, ssoProof, cancellationToken) is not { } challenge)
        {
            return null;
        }

        AuthMetrics.SignIn("hosted_mfa", "challenged");
        var result = SignInPageModel.Render(context, new SignInPageModel(enrolled ? SignInStep.Mfa : SignInStep.MfaEnroll, user.InstanceId, user.Email, next)
        {
            Challenge = challenge,
            AllowTotp = policy.AllowTotp,
            AllowPasskey = policy.AllowPasskey,
        });
        if (reservation is not null)
        {
            await reservation.ReturnAsync(cancellationToken);
        }

        return result;
    }

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

    public static async Task<IResult> SignedInAsync(HttpContext context, Sessions sessions, IssuedSession session, string? next, CancellationToken cancellationToken, AttemptLimiter.Reservation? reservation = null)
    {
        var tenant = Tenant(context);
        var sso = context.RequestServices.GetRequiredService<SsoSignIn>();
        if (!Tenancy.IsProject(tenant) && sso.ProofFor(context, session.Session.UserId, session.Session.AuthEpoch, session.Session.AuthStamp) is { } proof)
        {
            if (!await sso.FinishProofAsync(context, session, cancellationToken))
            {
                await sessions.RevokeAsync(tenant, session.Token, cancellationToken);
                return Refused(context);
            }

            next = proof.Next;
            AuthMetrics.SignIn("hosted_sso", "succeeded");
        }
        else if (!Tenancy.IsProject(tenant)
            && await context.RequestServices.GetRequiredService<Users>().GetAsync(tenant, session.Session.UserId, cancellationToken) is { } user
            && await SsoAsync(context, user.Email, next, cancellationToken) is { } enforced)
        {
            await sessions.RevokeAsync(tenant, session.Token, cancellationToken);
            return enforced;
        }

        if (!Tenancy.IsProject(tenant))
        {
            await sessions.RevokeAsync(tenant, context.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken);
        }
        await context.RequestServices.GetRequiredService<AccountProtection>().SignedInAsync(context, tenant, session.Session.UserId, cancellationToken);
        BrowserSecurity.SetSessionCookie(context.Response, session.Token, tenant);
        var result = SignInPageModel.Render(context, new SignInPageModel(next is null ? SignInStep.SignedIn : SignInStep.Continue, tenant, Next: next));
        if (reservation is not null)
        {
            await reservation.ReturnAsync(cancellationToken);
        }

        return result;
    }

    public static async Task<IResult?> ResumeAsync(HttpContext context, string? next, CancellationToken cancellationToken)
    {
        var tenant = Tenant(context);
        if (next is null || !next.StartsWith($"/{tenant}/oauth/authorize?", StringComparison.Ordinal)
            || next.Contains($"{AuthorizeEndpoint.LoginAfter}=", StringComparison.Ordinal)
            || context.Request.Cookies[Tenancy.SessionCookie(tenant)] is not { Length: > 0 } token)
        {
            return null;
        }

        try
        {
            return await context.RequestServices.GetRequiredService<Sessions>().ResolveAsync(tenant, token, cancellationToken) is null
                ? null : TypedResults.Redirect(next);
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            return null;
        }
    }

    public static async Task<IResult?> SsoAsync(HttpContext context, string email, string? next, CancellationToken cancellationToken)
    {
        var sso = context.RequestServices.GetRequiredService<SsoSignIn>();
        return !Tenancy.IsProject(Tenant(context)) && !await sso.ContinuesAsync(context, email, cancellationToken) && await sso.RequiredAsync(email, cancellationToken)
            ? SignInPageModel.Render(context, new SignInPageModel(SignInStep.Sso, Tenant(context), email, next) { SsoUrl = SsoSignIn.StartUrl(email, next) })
            : null;
    }

    public static async Task<bool> EnrolledAsync(HttpContext context, Sessions sessions, IssuedSession session, CancellationToken cancellationToken)
    {
        var tenant = Tenant(context);
        if (!Tenancy.IsProject(tenant))
        {
            if (!await context.RequestServices.GetRequiredService<SsoSignIn>().FinishProofAsync(context, session, cancellationToken))
            {
                await sessions.RevokeAsync(tenant, session.Token, cancellationToken);
                return false;
            }

            AuthMetrics.SignIn("hosted_sso", "succeeded");
            await sessions.RevokeAsync(tenant, context.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken);
            await context.RequestServices.GetRequiredService<AccountProtection>().SignedInAsync(context, tenant, session.Session.UserId, cancellationToken);
        }

        BrowserSecurity.SetSessionCookie(context.Response, session.Token, tenant);
        return true;
    }

    public static IResult UsernameTaken(HttpContext context, string email, string? next) =>
        SignInPageModel.Render(context, new SignInPageModel(SignInStep.SignUp, Tenant(context), email, next,
            Error: "Username taken. Choose another username."), StatusCodes.Status400BadRequest);

    public static bool WrongSlug(HttpContext context) => Tenancy.Resolved(context) is not { } tenant
        || !string.Equals(context.GetRouteValue("slug") as string, tenant, StringComparison.Ordinal)
        || !Allowed(context);

    private static bool Allowed(HttpContext context)
    {
        var theme = HostedTheme.Of(context);
        var page = (context.Request.Path.Value ?? string.Empty).Split('/', 4) is [_, _, _, var rest] ? rest.TrimEnd('/') : string.Empty;
        return page switch
        {
            "sign-in" => theme.Passcode || HttpMethods.IsGet(context.Request.Method)
                || PendingSocialLinks.Open(context.RequestServices.GetRequiredService<AuthOptions>(), Tenant(context),
                    PendingSocialLinks.Read(context, Tenant(context)), context.RequestServices.GetRequiredService<TimeProvider>()) is not null,
            "sign-in/verify" => theme.Passcode || theme.MagicLink
                || PendingSocialLinks.Open(context.RequestServices.GetRequiredService<AuthOptions>(), Tenant(context),
                    PendingSocialLinks.Read(context, Tenant(context)), context.RequestServices.GetRequiredService<TimeProvider>()) is not null,
            "sign-in/password" or "reset" or "reset/complete" or "reset/resend" => theme.Password,
            _ => true,
        };
    }

    public static bool HostedPage(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/ui")
        && HttpMethods.IsPost(context.Request.Method)
        && Tenancy.Resolved(context) is { Length: > 0 };

    public static string Tenant(HttpContext context) => Tenancy.Resolved(context) ?? throw new InvalidOperationException("The page has no tenant.");

    public static string DevicePath => $"/ui/{OidcIssuer.Slug}/device";

    public static string SecurityPath(string tenant) => $"/ui/{tenant}/security";

    public static string? SafeNext(HttpContext context, string? next) =>
        next is not null && Tenancy.Resolved(context) is { } tenant
            && (next.StartsWith($"/{tenant}/oauth/authorize?", StringComparison.Ordinal)
                || HostedTheme.Of(context).RedirectUrls.Contains(next, StringComparer.Ordinal)
                || Tenancy.IsProject(tenant) && next.StartsWith($"/ui/{tenant}/organization-invitations?token=", StringComparison.Ordinal)
                || Tenancy.IsProject(tenant) && (next == AgentEnrollments.PagePath(tenant) || next.StartsWith(AgentEnrollments.PagePath(tenant) + "?user_code=", StringComparison.Ordinal))
                || next == SecurityPath(tenant)
                || next == AuthOptions.DashboardOrigin + "/profile"
                || next == AuthOptions.DashboardOrigin + "/profile?tab=security"
                || !Tenancy.IsProject(tenant) && (next == DevicePath || next.StartsWith(DevicePath + "?", StringComparison.Ordinal)))
            && next.Length <= 4096
            && SignInPageModel.NavigationTarget(next) is not null
            && !next.Any(character => character is '\\' or '#' || char.IsControl(character) || char.IsWhiteSpace(character))
            ? next
            : null;

    public static async Task<SignInForm?> ReadAsync(HttpContext context, AuthOptions options, CancellationToken cancellationToken)
    {
        if (WrongSlug(context) || !context.Request.HasFormContentType
            || context.Request.Headers.Origin is not [{ } origin] || !IsPublicOrigin(origin, options.PublicOrigin))
        {
            return null;
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!HostedTheme.Of(context).Passcode && (context.Request.Path.Value ?? string.Empty).TrimEnd('/') is var path
            && (path.EndsWith("/sign-in", StringComparison.Ordinal) || path.EndsWith("/sign-in/verify", StringComparison.Ordinal)))
        {
            var tenant = Tenant(context);
            var clock = context.RequestServices.GetRequiredService<TimeProvider>();
            var pending = PendingSocialLinks.Open(options, tenant, PendingSocialLinks.Read(context, tenant), clock);
            var matchesPending = pending is not null && pending.Email == Users.NormalizeEmail(form["email"].ToString());
            var magic = path.EndsWith("/sign-in/verify", StringComparison.Ordinal) && HostedTheme.Of(context).MagicLink
                ? MagicLinks.Open(options, tenant, form["challenge"], clock) : null;
            if (!matchesPending && magic is null)
            {
                return null;
            }
        }

        return new SignInForm(form["email"], form["code"], form["next"], form["password"], form["pending_token"], form["challenge"], form["username"]);
    }

    public static bool IsPublicOrigin(string origin, Uri publicOrigin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
        && origin.Count(character => character == '/') == 2
        && string.IsNullOrEmpty(parsed.UserInfo) && string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment)
        && parsed.Scheme == publicOrigin.Scheme
        && string.Equals(parsed.IdnHost, publicOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && parsed.Port == publicOrigin.Port;

    public static IResult Refused(HttpContext context) => WrongSlug(context)
        ? TypedResults.NotFound()
        : SignInPageModel.Render(context, new SignInPageModel(SignInStep.Rejected, Tenant(context)), StatusCodes.Status403Forbidden);
}
