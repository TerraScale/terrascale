using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.WebAuthn;

internal static class PasskeyGuard
{
    internal static async Task<bool> LastFactorAsync(PlatformDirectory platform, TotpCredentials mfa, string userId, int remaining, CancellationToken cancellationToken)
    {
        if (remaining > 0 || (await mfa.StatusAsync(PlatformDirectory.Instance, userId, cancellationToken).ConfigureAwait(false)).Enabled)
        {
            return false;
        }

        string? after = null;
        do
        {
            var page = await platform.OrganizationsAsync(userId, 100, after, cancellationToken).ConfigureAwait(false);
            if (page.Items.Any(item => item.Organization.RequireMfa))
            {
                return true;
            }

            after = page.Next;
        }
        while (after is not null);

        return false;
    }

    internal static ApiException LastFactor() => new(StatusCodes.Status409Conflict, "last_factor",
        "This is your last passkey and an organization you belong to requires two-factor authentication; add another passkey or an authenticator app first.");
}
