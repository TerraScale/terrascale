using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

/// <summary>
/// <c>POST /ui/{slug}/sign-in/passkey/options</c>: the hosted sign-in step's WebAuthn assertion options for the address
/// the visitor entered: a sealed single-use challenge and that account's credential ids with user verification required.
/// </summary>
public sealed class PasskeySignInOptionsEndpoint(PasskeyCredentials passkeys, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/passkey/options");
        Name("ui_sign_in_passkey_options");
        Summary("Start a passkey sign-in");
        Description("Same-origin form post with the email address. Returns WebAuthn PublicKeyCredentialRequestOptions with a sealed single-use challenge valid 5 minutes, the account's credential ids as allowCredentials and userVerification required. An unknown address gets the same shape with an empty allowCredentials, so nothing reveals which addresses have accounts; the verification then fails the same way for every address. The challenge dies with the account's authentication state. Pass the answer to ui_sign_in_passkey.");
        Tags("Sign-in");
        // Accepts<T> describes a void body on this TerraScale.Endpoints version; EmptyRequest keeps the served schema unchanged.
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<PasskeyOptionsBody>(StatusCodes.Status200OK);
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
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

        var assertion = await passkeys.SignInOptionsAsync(SignInFlow.Tenant(this.HttpContext), form.Email ?? string.Empty, cancellationToken);
        return assertion is null
            ? throw ApiException.Unavailable()
            : Results.Json(new PasskeyOptionsBody(assertion.Value.Challenge, JsonDocument.Parse(assertion.Value.Options.ToJson()).RootElement),
                WebAuthnJson.Default.PasskeyOptionsBody);
    }
}
