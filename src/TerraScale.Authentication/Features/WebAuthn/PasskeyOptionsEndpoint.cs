using System.Text.Json;
using Fido2NetLib;
using TerraScale.Authentication.Contracts;
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
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        if (await passkeys.OptionsAsync(caller, cancellationToken).ConfigureAwait(false) is not { } options)
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before enrolling a passkey.");
        }

        return Results.Json(new PasskeyOptionsBody(options.Challenge, JsonDocument.Parse(options.Options.ToJson()).RootElement),
            WebAuthnJson.Default.PasskeyOptionsBody);
    }
}
