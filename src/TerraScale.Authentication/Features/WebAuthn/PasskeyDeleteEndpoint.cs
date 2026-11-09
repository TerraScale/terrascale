using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.WebAuthn;

public sealed class PasskeyDeleteEndpoint(PasskeyCredentials passkeys, TotpCredentials mfa, PlatformDirectory platform, StepUpProofs proofs, PlatformAuthentication authentication)
    : PlatformEndpoint(authentication)
{
    public override void Configure()
    {
        Delete("/v1/terrascale/account/passkeys/{id}");
        this.Describe("delete_passkey", "Delete a passkey",
            "Removes one enrolled passkey of the caller and releases its credential claim; the account's sessions and tokens stay. Requires a fresh approved delete_passkey/ts-auth proof in X-TerraScale-Step-Up-Proof, exactly as account deletion does. The write is conditional on the account's authentication state and the stored passkey list; a concurrent change answers 409 version_conflict, an unknown id 404 passkey_not_found. Deleting the last passkey of an account without an authenticator app that belongs to an organization requiring two-factor authentication answers 409 last_factor.",
            "Members", null, StatusCodes.Status204NoContent, null, roleChecked: false, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var id = this.HttpContext.GetRouteValue("id") is string value && value.Length is > 0 and <= 1024 ? value
            : throw new ApiException(StatusCodes.Status404NotFound, "passkey_not_found", "No passkey with this id is enrolled for the account.");
        var entries = await passkeys.ListAsync(caller, cancellationToken).ConfigureAwait(false);
        if (entries.All(entry => entry.CredentialId != id))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "passkey_not_found", "No passkey with this id is enrolled for the account.");
        }

        if (await PasskeyGuard.LastFactorAsync(platform, mfa, caller.Id, entries.Count - 1, cancellationToken).ConfigureAwait(false))
        {
            throw PasskeyGuard.LastFactor();
        }

        var verified = await proofs.ConsumeHeaderAsync(caller, this.HttpContext.Request, "delete_passkey", null, cancellationToken).ConfigureAwait(false);
        if (await passkeys.DeleteAsync(verified, id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new ApiException(403, "passkey_denied", "Sign in again before deleting a passkey.");
        }

        return Results.NoContent();
    }
}
