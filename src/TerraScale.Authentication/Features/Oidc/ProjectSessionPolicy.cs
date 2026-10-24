using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using TerraScale.Authentication.Features.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>Caps new and rotated project refresh tokens after OpenIddict applies its fixed family expiry.</summary>
public sealed class ProjectSessionPolicy(Sessions sessions, TimeProvider clock, IOpenIddictTokenManager tokens) : IOpenIddictServerHandler<ProcessSignInContext>
{
    /// <inheritdoc />
    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = Tenancy.Current(context.Transaction.GetHttpRequest()?.HttpContext);
        if (!Tenancy.IsProject(tenant) || context.RefreshTokenPrincipal is not { } refresh)
        {
            return;
        }

        if (ReadAuthenticationTime(refresh, clock) is not { } authenticatedAt)
        {
            context.Reject(Errors.InvalidGrant, "The sign-in has no valid authentication time.");
            return;
        }

        var lifetime = await sessions.GetLifetimeAsync(tenant, context.CancellationToken).ConfigureAwait(false);
        if (authenticatedAt > DateTimeOffset.MaxValue - lifetime || authenticatedAt + lifetime <= clock.GetUtcNow())
        {
            await RevokeAsync(tokens, refresh, context.CancellationToken).ConfigureAwait(false);
            context.Reject(Errors.InvalidGrant, "The project's absolute session lifetime has elapsed.");
            return;
        }

        var expires = authenticatedAt + lifetime;
        if (!context.Request.IsRefreshTokenGrantType() || refresh.GetExpirationDate() is not { } current || expires < current)
        {
            refresh.SetExpirationDate(expires);
        }
    }

    internal static DateTimeOffset? ReadAuthenticationTime(ClaimsPrincipal principal, TimeProvider clock) =>
        long.TryParse(principal.GetClaim(Claims.AuthenticationTime), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= 0 && value <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() && value <= clock.GetUtcNow().ToUnixTimeSeconds() + 30
            ? DateTimeOffset.FromUnixTimeSeconds(value) : null;

    internal static async ValueTask RevokeAsync(IOpenIddictTokenManager tokens, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.GetAuthorizationId() is { Length: > 0 } authorizationId)
        {
            // The existing store revokes the authorization, including descendants created by concurrent redemption.
            await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken).ConfigureAwait(false);
        }
    }
}
