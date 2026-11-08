using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
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
        Description("Same-origin fetch from the hosted security page with a live session cookie; returns the same WebAuthn creation options and sealed challenge as create_passkey_options, for the browser's navigator.credentials.create call. A cross-origin post or a missing session answers 403 passkey_denied.");
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

        if (!PasskeyHostedVerifyEndpoint.SameOrigin(this.HttpContext, options)
            || await SecurityFlow.LiveAsync(this.HttpContext, sessions, cancellationToken) is not { } live
            || await passkeys.OptionsAsync(live.User, cancellationToken).ConfigureAwait(false) is not { } created)
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before enrolling a passkey.");
        }

        return Results.Json(new PasskeyOptionsBody(created.Challenge, JsonDocument.Parse(created.Options.ToJson()).RootElement),
            WebAuthnJson.Default.PasskeyOptionsBody);
    }
}
