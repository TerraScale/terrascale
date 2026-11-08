using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyRenameEndpoint(PasskeyCredentials passkeys, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Patch("/v1/terrascale/account/passkeys/{id}");
        this.Describe("rename_passkey", "Rename a passkey",
            "Changes the caller-chosen name of one enrolled passkey, 1 to 64 characters. The write is conditional on the account's authentication state and the stored passkey list; a concurrent change answers 409 version_conflict, an unknown id 404 passkey_not_found.",
            "Members", typeof(PasskeyRenameRequest), StatusCodes.Status200OK, typeof(PasskeyItemBody), roleChecked: false, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(WebAuthnJson.Default.PasskeyRenameRequest, cancellationToken);
        if (body.Name is not { Length: > 0 and <= 64 })
        {
            throw Invalid("The passkey name must be 1 to 64 characters.");
        }

        var entry = await passkeys.RenameAsync(caller, this.CredentialId(), body.Name, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before renaming a passkey.");
        }

        return Results.Json(new PasskeyItemBody(entry.CredentialId, entry.Name, entry.Aaguid, entry.CreatedAt, entry.LastUsedAt), WebAuthnJson.Default.PasskeyItemBody);
    }

    private string CredentialId() =>
        this.HttpContext.GetRouteValue("id") is string value && value.Length is > 0 and <= 1024 ? value
            : throw new ApiException(StatusCodes.Status404NotFound, "passkey_not_found", "No passkey with this id is enrolled for the account.");
}
