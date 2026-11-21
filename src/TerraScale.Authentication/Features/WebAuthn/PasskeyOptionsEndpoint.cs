using System.Text.Json;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyOptionsEndpoint(PasskeyCredentials passkeys, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post("/v1/terrascale/account/passkeys/options");
        this.Describe("create_passkey_options", "Start passkey enrollment",
            "Returns WebAuthn PublicKeyCredentialCreationOptions for the caller with a sealed single-use challenge valid 5 minutes. Attestation is none, user verification is required and resident keys are preferred; existing credential ids are excluded. The challenge dies with the account's authentication state. Pass the answer to create_passkey_verify.",
            "Members", null, StatusCodes.Status200OK, typeof(PasskeyOptionsBody), roleChecked: false, scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        return await passkeys.OptionsAsync(caller, cancellationToken).ConfigureAwait(false) is { } options
            ? Results.Json(new PasskeyOptionsBody(options.Challenge, JsonDocument.Parse(options.Options.ToJson()).RootElement),
                WebAuthnJson.Default.PasskeyOptionsBody)
            : ErrorResults.ToResult(new Forbidden("Sign in again before enrolling a passkey.", "passkey_denied"));
    }
}
