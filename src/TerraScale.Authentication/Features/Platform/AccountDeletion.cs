using System.Text.Json;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// Hard-deletes a platform user's account (GDPR erasure): its organizations first (see
/// <see cref="PlatformDirectory.LeaveEverywhereAsync"/>), then the user with its email claim, then everything keyed by
/// the user.
/// </summary>
public sealed class AccountDeletion(
    PlatformDirectory platform,
    Users users,
    AuthStore store,
    IOpenIddictAuthorizationStore<OidcAuthorization> authorizations,
    IOpenIddictTokenStore<OidcToken> tokens,
    ILogger<AccountDeletion> logger)
{
    /// <summary>Deletes <paramref name="user"/>'s account; 409 <c>last_owner</c> deletes nothing.</summary>
    public async Task DeleteAsync(UserRecord user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        await platform.LeaveEverywhereAsync(user.Id, cancellationToken).ConfigureAwait(false);
        await users.DeleteAsync(user.InstanceId, user.Id, cancellationToken).ConfigureAwait(false);

        // Every session, passcode, MFA and token check reads the user first, so what a failure leaves behind grants nothing.
        try
        {
            await foreach (var document in store.QueryAsync(AuthStore.SessionCollection, user.InstanceId, "/userId", user.Id, cancellationToken).ConfigureAwait(false))
            {
                if (document.Document.Deserialize(IdentityJson.Default.SessionRecord) is { } session && session.UserId == user.Id)
                {
                    await store.CompareAndDeleteAsync(AuthStore.SessionCollection, user.InstanceId, session.TokenHash, document.Version, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var (collection, key) in new[] { (AuthStore.PasscodeCollection, user.Id), (AuthStore.PasscodeCollection, Passcodes.SignUpKey + user.Email), (AuthStore.MfaCollection, user.Id) })
            {
                if (await store.GetAsync(collection, user.InstanceId, key, cancellationToken).ConfigureAwait(false) is { } document)
                {
                    await store.CompareAndDeleteAsync(collection, user.InstanceId, key, document.Version, cancellationToken).ConfigureAwait(false);
                }
            }

            // Refresh tokens, device codes and sign-in entries, then the sign-ins (authorizations) they belong to.
            await foreach (var token in tokens.FindBySubjectAsync(user.Id, cancellationToken).ConfigureAwait(false))
            {
                await IgnoreRaceAsync(tokens.DeleteAsync(token, cancellationToken)).ConfigureAwait(false);
            }

            await foreach (var authorization in authorizations.FindBySubjectAsync(user.Id, cancellationToken).ConfigureAwait(false))
            {
                await IgnoreRaceAsync(authorizations.DeleteAsync(authorization, cancellationToken)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or ApiException)
        {
            logger.ZLogWarning($"Cleaning up after an account deletion failed ({error.GetType().Name:@Error}); what is left grants nothing");
        }
    }

    private static async Task IgnoreRaceAsync(ValueTask delete)
    {
        try
        {
            await delete.ConfigureAwait(false);
        }
        catch (OpenIddictExceptions.ConcurrencyException)
        {
            // Changed or deleted by a concurrent request; the user is already gone.
        }
    }
}
