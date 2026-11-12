using System.Globalization;
using System.Security.Claims;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Features.Oidc;

internal static class FixedRefreshTokens
{
    internal const string Parameter = "ts_refresh_expires_at";

    internal const string Claim = "ts_auth_refresh_expires_at";

    internal const string InvalidNotice = "ts_refresh_expires_at must be a Unix time in seconds after now and at most 365 days ahead.";

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);

    internal static bool TryRead(OpenIddictRequest request, string tenant, DateTimeOffset now, out DateTimeOffset? expiresAt)
    {
        expiresAt = null;
        if (!request.IsAuthorizationCodeGrantType() || request.ClientId != OidcIssuer.DashboardClientId || Tenancy.IsProject(tenant)
            || request.GetParameter(Parameter) is not { } requested)
        {
            return true;
        }

        if (!long.TryParse((string?)requested, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= now.ToUnixTimeSeconds() || seconds > (now + MaxLifetime).ToUnixTimeSeconds())
        {
            return false;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    internal static DateTimeOffset? ExpiresAt(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.GetClaim(Claim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    internal static bool Presented(ProcessSignInContext context) =>
        context.EndpointType is OpenIddictServerEndpointType.Token && context.Request.IsRefreshTokenGrantType()
        && ExpiresAt(context.Transaction.GetProperty<ProcessAuthenticationContext>(typeof(ProcessAuthenticationContext).FullName!)?.RefreshTokenPrincipal) is not null;
}

public sealed class KeepFixedRefreshToken : IOpenIddictServerHandler<ProcessSignInContext>
{
    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (FixedRefreshTokens.Presented(context))
        {
            context.GenerateRefreshToken = false;
            context.IncludeRefreshToken = false;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class ExpireFixedRefreshToken : IOpenIddictServerHandler<ProcessSignInContext>
{
    public ValueTask HandleAsync(ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RefreshTokenPrincipal is { } refresh && FixedRefreshTokens.ExpiresAt(context.Principal) is { } expiresAt)
        {
            refresh.SetExpirationDate(expiresAt);
        }

        return ValueTask.CompletedTask;
    }
}
