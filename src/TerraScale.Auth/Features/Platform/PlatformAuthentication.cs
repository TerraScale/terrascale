using System.Globalization;
using System.Net.Http.Headers;
using OpenIddict.Abstractions;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.Platform;

/// <summary>
/// Authenticates management API calls with the caller's own sign-in access token of the platform tenant: an ES256
/// <c>at+jwt</c> of the issuer, unexpired, whose <c>aud</c> includes the issuer. Only sign-in tokens carry that
/// audience, so organization, project, API key and service tokens are refused. The user, and the sign-in and token
/// entries the token names, are read from TerraBase on every request (the stores, never a cache), so a revoked sign-in
/// is refused at once on every replica; so is a token from before the user signed out everywhere or reset its password,
/// whose authentication epoch no longer matches the user's.
/// </summary>
public sealed class PlatformAuthentication(KeyRing ring, Users users, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, IOpenIddictTokenStore<OidcToken> tokens, AuthOptions options)
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
        var keys = await ring.GetActiveAsync(OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false);
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

        return claims is not null && Claim(claims, Claims.Subject) is { } userId
            && await users.GetAsync(OidcIssuer.InstanceId, userId, cancellationToken).ConfigureAwait(false) is { } user
            && Claim(claims, OidcIssuer.EpochClaim) == user.AuthEpoch.ToString(CultureInfo.InvariantCulture)
            && await this.LiveAsync(claims, userId, cancellationToken).ConfigureAwait(false)
            ? user
            : null;
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    // The token entry every sign-in access token names (oi_tkn_id; its jti is a separate value) and the sign-in
    // (authorization) it names must still be valid and the user's: revocation (RFC 7009) and refresh-token reuse revoke
    // them. A token naming no entry cannot be checked, so it is refused.
    private async Task<bool> LiveAsync(IDictionary<string, object> claims, string userId, CancellationToken cancellationToken)
    {
        if (Claim(claims, "oi_au_id") is { } authorizationId
            && (await authorizations.FindByIdAsync(authorizationId, cancellationToken).ConfigureAwait(false) is not { } authorization
                || authorization.Status != Statuses.Valid || authorization.Subject != userId))
        {
            return false;
        }

        return Claim(claims, "oi_tkn_id") is { } tokenId
            && await tokens.FindByIdAsync(tokenId, cancellationToken).ConfigureAwait(false) is { } entry
            && entry.Status == Statuses.Valid && entry.Subject == userId;
    }
}
