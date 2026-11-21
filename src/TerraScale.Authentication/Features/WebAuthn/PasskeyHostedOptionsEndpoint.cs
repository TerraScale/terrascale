using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyHostedOptionsEndpoint(Sessions sessions, PasskeyCredentials passkeys, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/passkeys/options");
        Name("ui_security_passkey_options");
        Summary("Start passkey enrollment from the security page");
        Description("Same-origin fetch from the hosted security page with a live session cookie opened within the last 10 minutes; an older session must sign in again before a passkey can be added. Returns the same WebAuthn creation options and sealed challenge as create_passkey_options, for the browser's navigator.credentials.create call. A cross-origin post or a missing session answers 403 passkey_denied.");
        Tags("Security");
        Produces<PasskeyOptionsBody>(StatusCodes.Status200OK, "application/json");
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (SignInFlow.WrongSlug(this.HttpContext))
        {
            return Results.NotFound();
        }

        var denied = new Forbidden("Sign in again before enrolling a passkey.", "passkey_denied");
        ChallengedSignIn? pending = null;
        if (this.HttpContext.Request.Headers[RequiredMfaEnrollment.Header] is [{ } challenge])
        {
            switch (await RequiredMfaEnrollment.ReadAsync(this.HttpContext, challenge, "passkey", cancellationToken))
            {
                case ChallengedSignIn allowed:
                    pending = allowed;
                    break;
                case Forbidden error:
                    return ErrorResults.ToResult(error);
            }
        }

        var live = pending is null ? await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) : null;
        return !PasskeyHostedVerifyEndpoint.SameOrigin(this.HttpContext, options)
            || pending is null && (live is null || !SecurityFlow.Recent(this.HttpContext, live.Value.Session, SecurityFlow.FactorEnrollmentWindow))
            || await passkeys.OptionsAsync(pending?.User ?? live!.Value.User, cancellationToken).ConfigureAwait(false) is not { } created
            ? ErrorResults.ToResult(denied)
            : Results.Json(new PasskeyOptionsBody(created.Challenge, JsonDocument.Parse(created.Options.ToJson()).RootElement),
                WebAuthnJson.Default.PasskeyOptionsBody);
    }
}
