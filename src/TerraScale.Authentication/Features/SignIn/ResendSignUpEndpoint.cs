using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>Resends a verification code without restarting the pending sign-up or reposting a password.</summary>
public sealed class ResendSignUpEndpoint(HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-up/resend");
        Name("ui_sign_up_resend");
        Summary("Resend a sign-up verification code");
        Description("Same-origin form post with email, pending_token and safe next. Uses the shared IP, mail cooldown and hourly budgets; 429 carries Retry-After. Retains the original pending token and password hash. Unknown, expired or superseded pending sign-ups have the same response; no password or account-existence detail is returned.");
        Tags("Sign-in");
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

        var email = form.Email?.Trim() ?? string.Empty;
        var next = SignInFlow.SafeNext(this.HttpContext, form.Next);
        var pending = form.PendingToken is { Length: 43 } token ? token : null;
        return await hosted.ResendSignUpAsync(email, pending ?? string.Empty, SignInFlow.Ip(this.HttpContext),
            AuthEmails.Locale(this.HttpContext.Request.Headers.AcceptLanguage), cancellationToken) is { } wait
            ? SignInFlow.Limited(this.HttpContext, SignInStep.SignUpCode, wait, email, next, pending)
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.SignUpCode, SignInFlow.Tenant(this.HttpContext), email, next)
            { PendingToken = pending, ResendAt = SignInFlow.ResendAt(this.HttpContext), Notice = "If this verification is still pending, a new code has been sent. Use the latest email." });
    }
}
