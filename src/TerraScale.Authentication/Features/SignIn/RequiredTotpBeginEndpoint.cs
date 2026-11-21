using Microsoft.AspNetCore.Http.Metadata;
using QRCoder;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class RequiredTotpBeginEndpoint(TotpCredentials mfa, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/enroll/totp");
        Name("ui_required_totp_begin");
        Summary("Enroll a required authenticator before sign-in");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
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
            ChallengedSignIn challenged => await EnrollAsync(challenged),
            Forbidden error => ErrorResults.ToResult(error),
        };

        async ValueTask<IResult> EnrollAsync(ChallengedSignIn challenged)
        {
            var enrollment = await mfa.BeginAsync(challenged.User, new MfaAuthorization(Change.Assert(challenged.User), challenged.ExpiresAt), cancellationToken);
            if (enrollment is null)
            {
                return ErrorResults.ToResult(new Conflict("An authenticator is already enrolled. Sign in again.", "mfa_enrolled"));
            }

            using var qr = QRCodeGenerator.GenerateQrCode(enrollment.ProvisioningUri, QRCodeGenerator.ECCLevel.M);
            return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.TotpSetup, challenged.User.InstanceId, challenged.User.Email,
                SignInFlow.SafeNext(this.HttpContext, form.Next))
            {
                Account = challenged.User.Email,
                Challenge = form.Challenge,
                Secret = enrollment.Secret,
                QrCode = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(qr).GetGraphic(6)),
            });
        }
    }
}
