using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class RequiredTotpConfirmEndpoint(Sessions sessions, TotpCredentials mfa, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/enroll/totp/confirm");
        Name("ui_required_totp_confirm");
        Summary("Confirm required MFA and finish sign-in");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var pending = await RequiredMfaEnrollment.ReadAsync(this.HttpContext, form.Challenge, "totp", cancellationToken);
        return pending switch
        {
            ChallengedSignIn challenged => await ConfirmAsync(challenged),
            Forbidden error => ErrorResults.ToResult(error),
        };

        async ValueTask<IResult> ConfirmAsync(ChallengedSignIn challenged)
        {
            var sso = this.HttpContext.RequestServices.GetRequiredService<SsoSignIn>();
            var viaSso = !Tenancy.IsProject(challenged.User.InstanceId);
            if (viaSso && !await sso.CanFinishAsync(this.HttpContext, challenged.User, cancellationToken))
            {
                return SignInFlow.Refused(this.HttpContext);
            }

            var code = form.Code?.Trim() ?? string.Empty;
            var authorization = new MfaAuthorization(Credentials.Claim(challenged.User.InstanceId, challenged.Completion), challenged.ExpiresAt);
            var issued = viaSso
                ? await mfa.ActivateAsync(challenged.User, code, authorization, cancellationToken)
                : await mfa.ConfirmAsync(challenged.User, code, authorization, cancellationToken);
            if (issued is null)
            {
                return ErrorResults.ToResult(new Forbidden("The authenticator code is wrong or expired.", "invalid_code"));
            }
            var completed = false;
            try
            {
                if (viaSso)
                {
                    await FactorEnrollmentNotice.SendAsync(this.HttpContext.RequestServices.GetRequiredService<IEmailSender>(), issued.User, cancellationToken);
                    sso.AdvanceEnrollment(this.HttpContext, challenged.User, issued.User);
                }

                var session = await sessions.CreateAsync(issued.User with { MultiFactor = true, MfaMethod = "totp" }, cancellationToken);
                completed = await SignInFlow.EnrolledAsync(this.HttpContext, sessions, session, cancellationToken);
            }
            catch (Exception) when (viaSso && !this.HttpContext.Response.HasStarted)
            {
                SsoSignIn.Clear(this.HttpContext);
                this.HttpContext.RequestServices.GetRequiredService<ILogger<RequiredTotpConfirmEndpoint>>()
                    .ZLogError($"SSO enrollment completion failed for user {challenged.User.Id:@UserId}, request {this.HttpContext.TraceIdentifier:@RequestId}");
            }

            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.RecoveryCodes, challenged.User.InstanceId,
                Next: completed ? SignInFlow.SafeNext(this.HttpContext, form.Next) : null,
                Error: completed ? null : SignInFlow.Copy(this.HttpContext).NoticeCredentialChanged)
            { Account = challenged.User.Email, RecoveryCodes = issued.Codes }, completed ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden);
        }
    }
}
