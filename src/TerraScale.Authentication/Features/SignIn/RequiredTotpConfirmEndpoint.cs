using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Endpoints;

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

        var issued = await mfa.ConfirmAsync(pending.User, form.Code?.Trim() ?? string.Empty,
            new MfaAuthorization(Credentials.Claim(pending.User.InstanceId, pending.Completion), pending.ExpiresAt), cancellationToken)
            ?? throw new ApiException(403, "invalid_code", "The authenticator code is wrong or expired.");
        var session = await sessions.CreateAsync(issued.User with { MultiFactor = true, MfaMethod = "totp" }, cancellationToken);
        if (viaSso)
        {
            sso.AdvanceEnrollment(this.HttpContext, pending.User, issued.User);
        }

        return await SignInFlow.EnrolledAsync(this.HttpContext, sessions, session, cancellationToken)
            ? SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.RecoveryCodes, pending.User.InstanceId,
                Next: SignInFlow.SafeNext(this.HttpContext, form.Next))
            { Account = pending.User.Email, RecoveryCodes = issued.Codes })
            : SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.RecoveryCodes, pending.User.InstanceId,
                Error: SignInFlow.Copy(this.HttpContext).NoticeCredentialChanged)
            { Account = pending.User.Email, RecoveryCodes = issued.Codes }, StatusCodes.Status403Forbidden);
    }
}
