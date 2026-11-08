using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyListEndpoint(PasskeyCredentials passkeys, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Get("/v1/terrascale/account/passkeys");
        this.Describe("list_passkeys", "List your passkeys",
            "The caller's enrolled passkeys: id, caller-chosen name, authenticator model (aaguid), creation time and last use. Public keys and other credential material are never returned.",
            "Members", null, StatusCodes.Status200OK, typeof(PasskeyListBody), roleChecked: false, scoped: false);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var entries = await passkeys.ListAsync(caller, cancellationToken).ConfigureAwait(false);
        return Results.Json(new PasskeyListBody([.. entries.Select(entry => new PasskeyItemBody(entry.CredentialId, entry.Name, entry.Aaguid, entry.CreatedAt, entry.LastUsedAt))]),
            WebAuthnJson.Default.PasskeyListBody);
    }
}
