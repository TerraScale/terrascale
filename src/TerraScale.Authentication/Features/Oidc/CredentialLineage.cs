using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Contracts;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Private reference-token proof ancestry; it is never published as an access or identity token claim.</summary>
internal static class CredentialLineage
{
    internal const string BindingsProperty = "ts-auth.credential-bindings";

    private const string Claim = "ts_auth_credential_bindings";

    internal const string ConflictNotice = "This credential expired, was already used, or lost a concurrent sign-in or rotation in another region. Sign in again or use the latest credential.";

    internal const string ReplayNotice = "This refresh token was already used. This sign-in has ended. Sign in again to continue.";

    /// <summary>The ancestry <see cref="RedeemExactlyOnce"/> resolved for the token request being served, if any.</summary>
    internal static IReadOnlyList<CredentialBinding> Issued(HttpContext? http) =>
        http?.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction?.GetProperty<IReadOnlyList<CredentialBinding>>(BindingsProperty) ?? [];

    internal static IReadOnlyList<CredentialBinding> Read(ClaimsPrincipal principal) =>
        principal.GetClaim(Claim) is { } json ? JsonSerializer.Deserialize(json, CredentialLineageJson.Default.CredentialBindingArray)
            ?? throw new JsonException("Missing credential ancestry.") : [];

    internal static void Attach(ClaimsPrincipal principal, IReadOnlyList<CredentialBinding> bindings) =>
        principal.SetClaim(Claim, bindings.Count == 0 ? null : JsonSerializer.Serialize(bindings.ToArray(), CredentialLineageJson.Default.CredentialBindingArray));

    internal static async ValueTask<bool> RevokeRefreshReplayAsync(IOpenIddictTokenStore<OidcToken> tokens, OidcToken? token,
        ClaimsPrincipal? principal, string? clientId, CancellationToken cancellationToken)
    {
        // Client authentication precedes refresh validation. OpenIddict's specialized presenter check runs later,
        // so establish that binding here before any side effect, using the integrity-validated principal.
        if (token is not { Type: TokenTypeIdentifiers.RefreshToken, AuthorizationId: { Length: > 0 } authorizationId }
            || principal is null || !principal.HasTokenType(TokenTypeIdentifiers.RefreshToken)
            || token.Id != principal.GetTokenId() || authorizationId != principal.GetAuthorizationId()
            || string.IsNullOrEmpty(clientId) || token.ApplicationId != clientId || !principal.HasPresenter(clientId))
        {
            return false;
        }

        // Ancestry rejection projects even spent entries as revoked. Require raw persisted redemption evidence;
        // an unused losing descendant or an explicitly revoked entry is insufficient to end the winning grant.
        if (token.Status != Statuses.Redeemed && (token.Status != Statuses.Revoked || tokens is not OidcTokenStore persisted
            || !await persisted.IsPersistedRefreshRedeemedAsync(token.Id!, authorizationId, clientId, cancellationToken).ConfigureAwait(false)))
        {
            return false;
        }

        // Revoke the grant itself: a concurrent request may still be creating descendants. Fail closed on storage failure.
        await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Validates stored ancestry and status, ending the grant on authenticated refresh-token replay.</summary>
public sealed class ValidateCredentialLineage(IOpenIddictTokenStore<OidcToken> tokens) : IOpenIddictServerHandler<ValidateTokenContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenId is not { } id)
        {
            return;
        }

        // An id_token_hint only names the user (RP-Initiated Logout 1.0 section 2): OpenIddict has checked its signature and
        // issuer and found its entry, and lifetime is not checked. ts-dashboard revokes its sign-in before redirecting here,
        // so a status check would turn every dashboard sign-out into a "Sign out?" page that leaves the session live (QA4 auth:11).
        // Clearing the authorization id skips OpenIddict's authorization-entry check for the same reason.
        if (context.EndpointType is OpenIddictServerEndpointType.EndSession && context.Principal?.HasTokenType(TokenTypeIdentifiers.IdentityToken) == true)
        {
            context.AuthorizationId = null;
            return;
        }

        var token = await tokens.FindByIdAsync(id, context.CancellationToken).ConfigureAwait(false);
        if (context.EndpointType is OpenIddictServerEndpointType.Token && context.Request is { } request && request.IsRefreshTokenGrantType()
            && await CredentialLineage.RevokeRefreshReplayAsync(tokens, token, context.Principal, request.ClientId,
                context.CancellationToken).ConfigureAwait(false))
        {
            context.Reject(Errors.InvalidToken, CredentialLineage.ReplayNotice);
            return;
        }

        switch (token?.Status)
        {
            case Statuses.Valid:
                return;
            case Statuses.Inactive:
                context.Reject(Errors.AuthorizationPending, "The device authorization is pending.");
                return;
            case Statuses.Rejected:
                context.Reject(Errors.AccessDenied, "The device authorization was denied.");
                return;
            default:
                context.Reject(context.Principal?.HasTokenType(TokenTypeIdentifiers.Private.ClientAssertion) == true
                    ? Errors.InvalidClient : Errors.InvalidToken, CredentialLineage.ConflictNotice);
                return;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CredentialBinding[]))]
internal sealed partial class CredentialLineageJson : JsonSerializerContext;
