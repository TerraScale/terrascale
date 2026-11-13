using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
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
        var sso = this.HttpContext.RequestServices.GetRequiredService<SsoSignIn>();
        var viaSso = !Tenancy.IsProject(pending.User.InstanceId);
        if (viaSso && !await sso.CanFinishAsync(this.HttpContext, pending.User, cancellationToken))
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var authorization = new MfaAuthorization(Credentials.Claim(pending.User.InstanceId, pending.Completion), pending.ExpiresAt);
        var issued = (viaSso
            ? await mfa.ActivateAsync(pending.User, form.Code?.Trim() ?? string.Empty, authorization, cancellationToken)
            : await mfa.ConfirmAsync(pending.User, form.Code?.Trim() ?? string.Empty, authorization, cancellationToken))
            ?? throw new ApiException(403, "invalid_code", "The authenticator code is wrong or expired.");
        var completed = false;
        try
        {
            if (viaSso)
            {
                await FactorEnrollmentNotice.SendAsync(this.HttpContext.RequestServices.GetRequiredService<IEmailSender>(), issued.User, cancellationToken);
                sso.AdvanceEnrollment(this.HttpContext, pending.User, issued.User);
            }

            var session = await sessions.CreateAsync(issued.User with { MultiFactor = true, MfaMethod = "totp" }, cancellationToken);
            completed = await SignInFlow.EnrolledAsync(this.HttpContext, sessions, session, cancellationToken);
        }
        catch (Exception) when (viaSso && !this.HttpContext.Response.HasStarted)
        {
            SsoSignIn.Clear(this.HttpContext);
            this.HttpContext.RequestServices.GetRequiredService<ILogger<RequiredTotpConfirmEndpoint>>()
                .ZLogError($"SSO enrollment completion failed for user {pending.User.Id:@UserId}, request {this.HttpContext.TraceIdentifier:@RequestId}");
        }

        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.RecoveryCodes, pending.User.InstanceId,
            Next: completed ? SignInFlow.SafeNext(this.HttpContext, form.Next) : null,
            Error: completed ? null : SignInFlow.Copy(this.HttpContext).NoticeCredentialChanged)
        { Account = pending.User.Email, RecoveryCodes = issued.Codes }, completed ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden);
    }
}
