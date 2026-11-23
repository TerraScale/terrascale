using System.Globalization;
using System.Net.Http.Headers;
using OpenIddict.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Platform;

public sealed class PlatformAuthentication(KeyRing ring, TotpCredentials mfa, IOpenIddictAuthorizationStore<OidcAuthorization> authorizations, IOpenIddictTokenStore<OidcToken> tokens, AuthOptions options, LiveAgents agents)
{
    private const int MaxTokenLength = 8 * 1024;

    public async Task<UserRecord?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken, string instanceId = OidcIssuer.InstanceId, string? audience = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: > 0 and <= MaxTokenLength } token)
        {
            return null;
        }

        var issuer = OidcIssuer.Issuer(options, instanceId).AbsoluteUri;
        IDictionary<string, object>? claims;
        var keys = await ring.GetPublicAsync(instanceId, Jwt.KeyId(token), cancellationToken);
        try
        {
            claims = await Jwt.ValidateAsync(token, keys, "at+jwt", issuer, audience ?? issuer);
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

        var userRead = mfa.PrimaryUserAsync(instanceId, userId, Claim(claims, OidcIssuer.EpochClaim), Claim(claims, OidcIssuer.StateClaim),
            Claim(claims, OidcIssuer.MfaClaim), cancellationToken);
        var liveRead = this.LiveAsync(claims, userId, cancellationToken);
        var agentRead = Claim(claims, LiveAgents.Claim) is { } agentId ? agents.ActiveAsync(agentId, userId, cancellationToken) : null;
        await Task.WhenAll(userRead, liveRead, agentRead ?? Task.CompletedTask);
        return await liveRead is { } entry && await userRead is { } user
            && (agentRead is null || await agentRead is not null)
            ? user with { AuthenticationBindings = entry.Bindings, SessionId = Claim(claims, "ts_auth_session"), ActingAgentId = Claim(claims, LiveAgents.Claim) }
            : null;
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;

    private async Task<OidcToken?> LiveAsync(IDictionary<string, object> claims, string userId, CancellationToken cancellationToken)
    {
        if (Claim(claims, "oi_tkn_id") is not { } tokenId)
        {
            return null;
        }

        var authorizationId = Claim(claims, "oi_au_id");
        var authorizationRead = authorizationId is null ? Task.FromResult<OidcAuthorization?>(null) : authorizations.FindByIdAsync(authorizationId, cancellationToken).AsTask();
        var entryRead = tokens.FindByIdAsync(tokenId, cancellationToken).AsTask();
        await Task.WhenAll(authorizationRead, entryRead);
        return (authorizationId is null
                || await authorizationRead is { } authorization && authorization.Status == Statuses.Valid && authorization.Subject == userId)
            && await entryRead is { } entry && entry.Status == Statuses.Valid && entry.Subject == userId
                ? entry
                : null;
    }
}
