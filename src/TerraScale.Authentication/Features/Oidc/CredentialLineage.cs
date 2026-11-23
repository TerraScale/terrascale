using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIddict.Abstractions;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TerraScale.Authentication.Contracts;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

internal static class CredentialLineage
{
    internal const string BindingsProperty = "ts-auth.credential-bindings";

    internal const string RedeemedCodeProperty = "ts-auth.redeemed-code";

    private const string Claim = "ts_auth_credential_bindings";

    internal const string ConflictNotice = "This credential expired, was already used, or lost a concurrent sign-in or rotation in another region. Sign in again or use the latest credential.";

    internal const string ReplayNotice = "This credential was already used, so its sign-in was ended for safety. Sign in again.";

    internal static IReadOnlyList<CredentialBinding> Issued(HttpContext? http) =>
        http?.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction?.GetProperty<IReadOnlyList<CredentialBinding>>(BindingsProperty) ?? [];

    internal static IReadOnlyList<CredentialBinding> Read(ClaimsPrincipal principal) =>
        principal.GetClaim(Claim) is { } json ? JsonSerializer.Deserialize(json, CredentialLineageJson.Default.CredentialBindingArray)
            ?? throw new JsonException("Missing credential ancestry.") : [];

    internal static void Attach(ClaimsPrincipal principal, IReadOnlyList<CredentialBinding> bindings) =>
        principal.SetClaim(Claim, bindings.Count == 0 ? null : JsonSerializer.Serialize(bindings.ToArray(), CredentialLineageJson.Default.CredentialBindingArray));

    internal static async ValueTask<bool> RevokeReplayAsync(IOpenIddictTokenStore<OidcToken> tokens, OidcToken? token,
        ClaimsPrincipal? principal, string? clientId, CancellationToken cancellationToken)
    {
        if (token is not { AuthorizationId: { Length: > 0 } authorizationId }
            || principal is null || token.Id != principal.GetTokenId() || authorizationId != principal.GetAuthorizationId()
            || string.IsNullOrEmpty(clientId) || token.ApplicationId != clientId || !principal.HasPresenter(clientId))
        {
            return false;
        }

        if ((token.Type != TokenTypeIdentifiers.RefreshToken && token.Type != TokenTypeIdentifiers.Private.AuthorizationCode)
            || !principal.HasTokenType(token.Type))
        {
            return false;
        }

        var redeemed = token.Status == Statuses.Redeemed
            || (token.Type == TokenTypeIdentifiers.RefreshToken
                && token.Status == Statuses.Revoked && tokens is OidcTokenStore persisted
                && await persisted.IsPersistedRefreshRedeemedAsync(token.Id!, authorizationId, clientId, cancellationToken));
        if (!redeemed)
        {
            return false;
        }

        await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);
        return true;
    }
}

public sealed class ValidateCredentialLineage(IOpenIddictTokenStore<OidcToken> tokens) : IOpenIddictServerHandler<ValidateTokenContext>
{
    public async ValueTask HandleAsync(ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TokenId is not { } id)
        {
            return;
        }

        if (context.EndpointType is OpenIddictServerEndpointType.EndSession && context.Principal?.HasTokenType(TokenTypeIdentifiers.IdentityToken) == true)
        {
            context.AuthorizationId = null;
            return;
        }

        var token = await tokens.FindByIdAsync(id, context.CancellationToken);
        if (context.EndpointType is OpenIddictServerEndpointType.Token && context.Request?.IsAuthorizationCodeGrantType() == true
            && token is { Type: TokenTypeIdentifiers.Private.AuthorizationCode, Status: Statuses.Redeemed })
        {
            context.Transaction.SetProperty(CredentialLineage.RedeemedCodeProperty, string.Empty);
            return;
        }

        if (context.EndpointType is OpenIddictServerEndpointType.Token && context.Request is { } request && request.IsRefreshTokenGrantType()
            && await CredentialLineage.RevokeReplayAsync(tokens, token, context.Principal, request.ClientId,
                context.CancellationToken))
        {
            context.Reject(OidcErrors.InvalidToken, CredentialLineage.ReplayNotice);
            return;
        }

        switch (token?.Status)
        {
            case Statuses.Valid:
                return;
            case Statuses.Inactive:
                context.Reject(OidcErrors.AuthorizationPending, "The device authorization is pending.");
                return;
            case Statuses.Rejected:
                context.Reject(OidcErrors.AccessDenied, "The device authorization was denied.");
                return;
            default:
                context.Reject(context.Principal?.HasTokenType(TokenTypeIdentifiers.Private.ClientAssertion) == true
                    ? OidcErrors.InvalidClient : OidcErrors.InvalidToken, CredentialLineage.ConflictNotice);
                return;
        }
    }
}

public sealed class ReportCodeReplayNotice : IOpenIddictServerHandler<ApplyTokenResponseContext>
{
    public ValueTask HandleAsync(ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.Error != OidcErrors.InvalidGrant
            || context.Response.ErrorDescription is not ({ Length: > 0 } description)
            || description is CredentialLineage.ReplayNotice or CredentialLineage.ConflictNotice
            || context.Transaction.GetProperty<string>(CredentialLineage.RedeemedCodeProperty) is null)
        {
            return ValueTask.CompletedTask;
        }

        context.Response.ErrorDescription = CredentialLineage.ReplayNotice;
        return ValueTask.CompletedTask;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CredentialBinding[]))]
internal sealed partial class CredentialLineageJson : JsonSerializerContext;
