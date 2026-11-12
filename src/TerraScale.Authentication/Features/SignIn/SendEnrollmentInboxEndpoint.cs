using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class SendEnrollmentInboxEndpoint(AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/mfa/enroll/email");
        Name("ui_required_mfa_send_email");
        Summary("Resend inbox proof for required MFA enrollment");
        Description("Same-origin form post. Requires an unexpired enrollment-inbox challenge from primary authentication for a project that still requires MFA and a user without an allowed factor. Issues a fresh challenge and purpose-specific code, subject to the shared hosted email cooldown and hourly budgets. Never opens a session.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is not { } form)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        var pending = await VerifyEnrollmentInboxEndpoint.PendingAsync(this.HttpContext, form.Challenge, cancellationToken);
        return await VerifyEnrollmentInboxEndpoint.SendAsync(this.HttpContext, pending.User, SignInFlow.SafeNext(this.HttpContext, form.Next), cancellationToken);
    }
}
