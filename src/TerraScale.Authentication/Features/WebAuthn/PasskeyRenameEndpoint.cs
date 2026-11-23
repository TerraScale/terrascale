using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyRenameEndpoint(PasskeyCredentials passkeys, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Patch("/v1/terrascale/account/passkeys/{id}");
        this.Describe("rename_passkey", "Rename a passkey",
            "Changes the caller-chosen name of one enrolled passkey, 1 to 64 characters. The write is conditional on the account's authentication state and the stored passkey list; a concurrent change answers 409 version_conflict, an unknown id 404 passkey_not_found, a stale account state 403 passkey_denied.",
            "Members", typeof(PasskeyRenameRequest), StatusCodes.Status200OK, typeof(PasskeyItemBody), roleChecked: false, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(WebAuthnJson.Default.PasskeyRenameRequest, cancellationToken);
        if (body.Name is not { Length: > 0 and <= 64 })
        {
            return ErrorResults.ToResult(new Validation("The passkey name must be 1 to 64 characters."));
        }

        if (this.HttpContext.GetRouteValue("id") is not (string id and { Length: > 0 and <= 1024 }))
        {
            return ErrorResults.ToResult(new NotFound("No passkey with this id is enrolled for the account.", "passkey_not_found"));
        }

        return await passkeys.RenameAsync(caller, id, body.Name, cancellationToken) switch
        {
            PasskeyCredentialEntry entry => TypedResults.Json(new PasskeyItemBody(entry.CredentialId, entry.Name, entry.Aaguid, entry.CreatedAt, entry.LastUsedAt),
                WebAuthnJson.Default.PasskeyItemBody),
            Forbidden error => ErrorResults.ToResult(error),
            NotFound error => ErrorResults.ToResult(error),
            Conflict error => ErrorResults.ToResult(error),
        };
    }
}
