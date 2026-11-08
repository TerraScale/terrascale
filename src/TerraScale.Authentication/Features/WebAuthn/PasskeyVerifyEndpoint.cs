using Fido2NetLib;
using Fido2NetLib.Objects;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyVerifyEndpoint(PasskeyCredentials passkeys, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Post("/v1/terrascale/account/passkeys/verify");
        this.Describe("create_passkey_verify", "Finish passkey enrollment",
            "Verifies the browser's WebAuthn registration answer (origin, RP id, user verification, challenge) against the sealed challenge from create_passkey_options and stores the passkey. The challenge works once and dies with the account's authentication state; a credential id another account already enrolled answers 409 passkey_taken; a reused or stale challenge answers 403 passkey_denied.",
            "Members", typeof(PasskeyVerifyRequest), StatusCodes.Status201Created, typeof(PasskeyEnrolledBody), roleChecked: false, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(WebAuthnJson.Default.PasskeyVerifyRequest, cancellationToken);
        var raw = Map(body.Response);
        var entry = raw is null ? null
            : await passkeys.VerifyAsync(caller, body.Challenge, raw, body.Name is { Length: > 0 and <= 100 } ? body.Name : null, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new ApiException(403, "passkey_denied", "The passkey registration failed verification; start again.");
        }

        return Results.Json(new PasskeyEnrolledBody(entry.CredentialId, entry.Name, entry.Aaguid, entry.CreatedAt),
            WebAuthnJson.Default.PasskeyEnrolledBody, statusCode: StatusCodes.Status201Created);
    }

    private static AuthenticatorAttestationRawResponse? Map(PasskeyAttestationRequest? response)
    {
        if (response?.Response is not { } inner
            || response.RawId is not { Length: > 0 }
            || response.Type?.Equals("public-key", StringComparison.Ordinal) != true
            || inner.ClientDataJson is not { Length: > 0 } || inner.AttestationObject is not { Length: > 0 })
        {
            return null;
        }

        AuthenticatorTransport[] transports = [.. (inner.Transports ?? []).Select(transport => Enum.TryParse<AuthenticatorTransport>(transport, ignoreCase: true, out var parsed) ? parsed : throw new ApiException(400, "invalid_request", "Unknown passkey transport."))];
        return new AuthenticatorAttestationRawResponse
        {
            Id = response.Id ?? string.Empty,
            RawId = PasskeyCredentials.Base64UrlDecode(response.RawId),
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                ClientDataJson = PasskeyCredentials.Base64UrlDecode(inner.ClientDataJson),
                AttestationObject = PasskeyCredentials.Base64UrlDecode(inner.AttestationObject),
                Transports = transports,
            },
        };
    }
}
