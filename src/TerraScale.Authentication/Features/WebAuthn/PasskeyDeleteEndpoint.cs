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
            "Members", null, StatusCodes.Status204NoContent, null, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        if (this.HttpContext.GetRouteValue("id") is not (string value and { Length: > 0 and <= 1024 }))
        {
            return ErrorResults.ToResult(new AuthNotFound("No passkey with this id is enrolled for the account.", "passkey_not_found"));
        }

        var id = value;
        var entries = await passkeys.ListAsync(caller, cancellationToken);
        if (entries.All(entry => entry.CredentialId != id))
        {
            return ErrorResults.ToResult(new AuthNotFound("No passkey with this id is enrolled for the account.", "passkey_not_found"));
        }

        if (await PasskeyGuard.LastFactorAsync(platform, mfa, caller.Id, entries.Count - 1, cancellationToken))
        {
            return ErrorResults.ToResult(PasskeyGuard.LastFactor);
        }

        var verified = await proofs.ConsumeHeaderAsync(caller, this.HttpContext.Request, "delete_passkey", null, cancellationToken);
        return await passkeys.DeleteAsync(verified, id, cancellationToken) switch
        {
            PasskeyCredentialEntry => TypedResults.NoContent(),
            AuthForbidden error => ErrorResults.ToResult(error),
            AuthNotFound error => ErrorResults.ToResult(error),
            AuthConflict error => ErrorResults.ToResult(error),
        };
    }
}
