using System.Globalization;
using System.Net.Http.Headers;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// Authenticates management API calls with the caller's own sign-in access token of the platform tenant: an ES256
/// <c>at+jwt</c> of the issuer, unexpired, whose <c>aud</c> includes the issuer. Only sign-in tokens carry that
/// audience, so organization, project, API key and service tokens are refused. The user, and the sign-in and token
/// entries the token names, are read from TerraBase on every request (the stores, never a cache), so a revoked sign-in
/// is refused at once on every replica; so is a token from before the user signed out everywhere or reset its password,
/// whose authentication epoch no longer matches the user's.
/// </summary>
public sealed class PlatformAuthentication(KeyRing ring, TotpCredentials mfa, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, IOpenIddictTokenStore<OidcToken> tokens, AuthOptions options)
{
    private const int MaxTokenLength = 8 * 1024;

    /// <summary>The signed-in user, or null when the request carries no valid sign-in token.</summary>
    public async Task<UserRecord?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: > 0 and <= MaxTokenLength } token)
        {
            return null;
        }

        var issuer = OidcIssuer.Issuer(options).AbsoluteUri;
        IDictionary<string, object>? claims;
        var keys = await ring.GetPublicAsync(OidcIssuer.InstanceId, Jwt.KeyId(token), cancellationToken).ConfigureAwait(false);
        try
        {
            claims = await Jwt.ValidateAsync(token, keys, "at+jwt", issuer, issuer).ConfigureAwait(false);
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }

        if (claims is null || Claim(claims, Claims.Subject) is not { } userId)
        {
            return null;
        }

        // The sign-in and token entry reads run alongside the factor read and the user read that follows it: two storage
        // round trips instead of five.
        var userRead = mfa.PrimaryUserAsync(OidcIssuer.InstanceId, userId, Claim(claims, OidcIssuer.EpochClaim), Claim(claims, OidcIssuer.StateClaim),
            Claim(claims, OidcIssuer.MfaClaim) is not null, cancellationToken);
        var liveRead = this.LiveAsync(claims, userId, cancellationToken);
        await Task.WhenAll(userRead, liveRead).ConfigureAwait(false);
        return await liveRead.ConfigureAwait(false) is { } entry && await userRead.ConfigureAwait(false) is { } user
            ? user with { AuthenticationBindings = entry.Bindings }
            : null;
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    // The token entry every sign-in access token names (oi_tkn_id; its jti is a separate value) and the sign-in
    // (authorization) it names must still be valid and the user's: revocation (RFC 7009) and refresh-token reuse revoke
    // them. A token naming no entry cannot be checked, so it is refused.
    private async Task<OidcToken?> LiveAsync(IDictionary<string, object> claims, string userId, CancellationToken cancellationToken)
    {
        if (Claim(claims, "oi_tkn_id") is not { } tokenId)
        {
            return null;
        }

        // Both entries are named by the token, so they are read together.
        var authorizationId = Claim(claims, "oi_au_id");
        var authorizationRead = authorizationId is null ? Task.FromResult<OidcAuthorization?>(null) : authorizations.FindByIdAsync(authorizationId, cancellationToken).AsTask();
        var entryRead = tokens.FindByIdAsync(tokenId, cancellationToken).AsTask();
        await Task.WhenAll(authorizationRead, entryRead).ConfigureAwait(false);
        return (authorizationId is null
                || await authorizationRead.ConfigureAwait(false) is { } authorization && authorization.Status == Statuses.Valid && authorization.Subject == userId)
            && await entryRead.ConfigureAwait(false) is { } entry && entry.Status == Statuses.Valid && entry.Subject == userId
                ? entry
                : null;
    }
}
