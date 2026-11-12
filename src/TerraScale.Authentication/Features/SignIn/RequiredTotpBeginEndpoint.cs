using Microsoft.AspNetCore.Http.Metadata;
using QRCoder;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
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
        var enrollment = await mfa.BeginAsync(pending.User, new MfaAuthorization(Change.Assert(pending.User), pending.ExpiresAt), cancellationToken)
            ?? throw new ApiException(409, "mfa_enrolled", "An authenticator is already enrolled. Sign in again.");
        using var qr = QRCodeGenerator.GenerateQrCode(enrollment.ProvisioningUri, QRCodeGenerator.ECCLevel.M);
        return SignInPageModel.Render(this.HttpContext, new SignInPageModel(SignInStep.TotpSetup, pending.User.InstanceId, pending.User.Email,
            SignInFlow.SafeNext(this.HttpContext, form.Next))
        {
            Account = pending.User.Email,
            Challenge = form.Challenge,
            Secret = enrollment.Secret,
            QrCode = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(qr).GetGraphic(6)),
        });
    }
}
