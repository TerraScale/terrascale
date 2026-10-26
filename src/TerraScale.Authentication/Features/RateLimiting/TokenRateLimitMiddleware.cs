using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;

using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.RateLimiting;

/// <summary>
/// Charges every token POST to the socket peer's shared minute budget before forwarding and OpenIddict checks credentials.
/// Explicit trusted proxies also charge a separate resolved-client budget after forwarding.
/// IPv4-mapped addresses share the IPv4 budget; IPv6 shares a /64 and an additional /48 budget.
/// Untrusted forwarding headers and submitted client identifiers never select a bucket. Missing peers and storage failures
/// fail closed with an OAuth error.
/// </summary>
public sealed class TokenRateLimitMiddleware(RequestDelegate next, AttemptLimiter limiter, AuthOptions options, bool clientLimit)
{
    /// <summary>Trusted proxies have this many clients' token capacity in their aggregate socket budget.</summary>
    public const int ProxyFactor = 4;

    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";

    // The device authorization endpoint writes two tokens per unauthenticated call, so it shares the token budget.
    private const string DevicePath = "/" + OidcIssuer.Slug + "/oauth/device";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly object TrustedProxyKey = new();

    /// <summary>Registers socket-peer admission before forwarded headers are processed.</summary>
    public static IApplicationBuilder UsePeerGuard(IApplicationBuilder app) =>
        app.UseMiddleware<TokenRateLimitMiddleware>(false);

    /// <summary>Registers resolved-client admission after forwarding and before client authentication.</summary>
    public static IApplicationBuilder UseClientGuard(IApplicationBuilder app) =>
        app.UseMiddleware<TokenRateLimitMiddleware>(true);

    /// <summary>Runs the guard for the real issuer's token endpoint, then the authentication pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // OpenIddict treats casing and a single trailing slash as equivalent endpoint paths.
        if (!HttpMethods.IsPost(context.Request.Method)
            || !(context.Request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(DevicePath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(DevicePath + "/", StringComparison.OrdinalIgnoreCase)
                || IsProjectTokenPath(context.Request.Path)
                || EndUserTokenEndpoint.Matches(context.Request.Path)))
        {
            await next(context);
            return;
        }

        IResult? refusal;
        try
        {
            refusal = await this.LimitAsync(context) is { } wait ? HostedAuth.TooMany(wait) : null;
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.CacheControl = "no-store";
            refusal = EndUserTokenEndpoint.Matches(context.Request.Path)
                ? Results.Json(new ErrorBody(new ErrorDetail("temporarily_unavailable", "The token request could not be checked; retry shortly.")),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, "The token request could not be checked; retry shortly."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (refusal is not null)
        {
            await refusal.ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    // A project tenant's token endpoint, existing or not, charges the same per-peer budget as the platform's: the
    // budget belongs to the caller's address, not to the tenant it names.
    internal static bool IsProjectTokenPath(PathString path) =>
        (path.Value ?? string.Empty).Split('/') is [_, var tenant, var oauth, var token, ..] rest
        && rest.Length <= 5 && (rest.Length == 4 || rest[4].Length == 0)
        && string.Equals(oauth, "oauth", StringComparison.OrdinalIgnoreCase) && string.Equals(token, "token", StringComparison.OrdinalIgnoreCase)
        && TerraScaleIds.IsValid(tenant);

    private async Task<TimeSpan?> LimitAsync(HttpContext context)
    {
        // Only the guard before forwarding can mark the actual socket peer as trusted. No request header supplies this marker.
        if (clientLimit && !context.Items.ContainsKey(TrustedProxyKey))
        {
            return null;
        }

        var peer = context.Connection.RemoteIpAddress;
        var subject = HostedAuth.Subject(peer?.ToString()) ?? throw ApiException.Unavailable();
        var maximum = options.RateLimitPerWindow;
        var purpose = clientLimit ? "token-client-ip" : "token-peer-ip";
        if (!clientLimit && options.TrustedProxies.Contains(peer!.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer))
        {
            context.Items[TrustedProxyKey] = true;
            maximum = (int)Math.Min((long)maximum * ProxyFactor, int.MaxValue);
            // Proxy and ordinary peer capacities must not compete in a shared IPv6 prefix or folded bucket.
            purpose = "token-proxy-ip";
        }

        if (await limiter.AcquireAsync(OidcIssuer.InstanceId, purpose, limiter.Fold(subject), maximum, Window, context.RequestAborted) is { } wait)
        {
            return wait;
        }

        if (IPAddress.Parse(subject) is { AddressFamily: AddressFamily.InterNetworkV6 } address)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 6, bytes.Length - 6);
            // AttemptLimiter accepts Int32 capacities; avoid overflow for a large configured peer budget.
            var siteMaximum = (int)Math.Min((long)maximum * HostedAuth.SiteFactor, int.MaxValue);
            return await limiter.AcquireAsync(OidcIssuer.InstanceId, purpose + "-site", limiter.Fold(new IPAddress(bytes).ToString()), siteMaximum, Window, context.RequestAborted);
        }

        return null;
    }
}
