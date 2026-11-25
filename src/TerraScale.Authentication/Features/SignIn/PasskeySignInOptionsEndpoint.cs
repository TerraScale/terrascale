using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.WebAuthn;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class PasskeySignInOptionsEndpoint(PasskeyCredentials passkeys, HostedAuth hosted, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/sign-in/passkey/options");
        Name("ui_sign_in_passkey_options");
        Summary("Start a passkey sign-in");
        Description("Same-origin form post. Returns WebAuthn PublicKeyCredentialRequestOptions with a sealed single-use challenge valid 5 minutes, an empty allowCredentials so the authenticator offers its discoverable passkeys, and userVerification required. The answer never reads the account directory, so it is identical for every address and reveals nothing about which addresses have accounts or passkeys. Pass the answer to ui_sign_in_passkey.");
        Tags("Sign-in");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<PasskeyOptionsBody>(StatusCodes.Status200OK);
        Produces<string>(StatusCodes.Status403Forbidden, "text/html");
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is null)
        {
            return SignInFlow.Refused(this.HttpContext);
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many sign-in attempts; retry after the Retry-After delay.", wait);
        }

        var (assertion, challenge) = passkeys.SignInOptions(SignInFlow.Tenant(this.HttpContext));
        return TypedResults.Json(new PasskeyOptionsBody(challenge, JsonDocument.Parse(assertion.ToJson()).RootElement), WebAuthnJson.Default.PasskeyOptionsBody);
    }
}
