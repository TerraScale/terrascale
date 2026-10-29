using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Ends the sign-in behind an authorization code presented again after it was redeemed (RFC 6749 §4.1.2): the code's
/// authorization is revoked, so every refresh token issued from it is refused and ts-auth stops accepting its access
/// tokens. Only a request that could itself have redeemed the code (same client, redirect URI and PKCE verifier)
/// triggers this, and only with persisted proof of the redemption; anything else is refused without side effects.
/// </summary>
internal static class AuthorizationCodeReplay
{
    internal const string Notice = "This authorization code was already used. Its sign-in has ended. Sign in again to continue.";

    /// <summary>Revokes the code's authorization and returns true for a genuine replay of <paramref name="principal"/>'s code.</summary>
    internal static async ValueTask<bool> EndGrantAsync(OpenIddictServerEvents.BaseContext context, ClaimsPrincipal? principal, AuthStore store,
        IOpenIddictTokenStore<OidcToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The integrity-validated code principal carries the bindings OpenIddict checks only after token validation.
        if (context.Transaction.EndpointType is not OpenIddictServerEndpointType.Token || context.Transaction.Request is not { } request || !request.IsAuthorizationCodeGrantType()
            || principal is null || !principal.HasTokenType(TokenTypeIdentifiers.Private.AuthorizationCode)
            || principal.GetTokenId() is not { Length: > 0 } id || principal.GetAuthorizationId() is not { Length: > 0 } authorizationId
            || request.ClientId is not { Length: > 0 } clientId || !principal.HasPresenter(clientId)
            || principal.GetClaim(Claims.Private.RedirectUri) is { } redirectUri && redirectUri != request.RedirectUri
            || !VerifierMatches(principal, request.CodeVerifier)
            || context.Transaction.GetProperty<string>(TrustPublicOrigin.TenantProperty) is not { } tenant)
        {
            return false;
        }

        // The token store projects a lost ancestry as revoked; only the stored redemption proves this code was used.
        var stored = (await store.GetAsync(AuthStore.OidcTokenCollection, tenant, id, context.CancellationToken).ConfigureAwait(false))?.Document
            .Deserialize(OidcJson.Default.OidcToken);
        if (stored is not { Status: Statuses.Redeemed, Type: TokenTypeIdentifiers.Private.AuthorizationCode } || stored.Id != id || stored.InstanceId != tenant
            || stored.AuthorizationId != authorizationId || stored.ApplicationId != clientId)
        {
            return false;
        }

        // Revoke the grant itself: a concurrent redemption may still be issuing its tokens. A storage failure fails closed.
        await tokens.RevokeByAuthorizationIdAsync(authorizationId, context.CancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool VerifierMatches(ClaimsPrincipal principal, string? verifier) =>
        principal.GetClaim(Claims.Private.CodeChallenge) is { Length: > 0 } challenge
        && principal.GetClaim(Claims.Private.CodeChallengeMethod) == CodeChallengeMethods.Sha256
        && verifier is { Length: >= 43 and <= 128 }
        && Tokens.FixedTimeEquals(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);
}
