using System.Net;
using System.Net.Sockets;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.RateLimiting;

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
                || context.Request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        IResult? refusal;
        try
        {
            refusal = await this.LimitAsync(context) is { } wait ? HeadlessAuth.TooMany(wait) : null;
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable)
        {
            context.Response.Headers.CacheControl = "no-store";
            refusal = Results.Json(new OAuthErrorBody(Errors.TemporarilyUnavailable, "The token request could not be checked; retry shortly."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (refusal is not null)
        {
            await refusal.ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private async Task<TimeSpan?> LimitAsync(HttpContext context)
    {
        // Only the guard before forwarding can mark the actual socket peer as trusted. No request header supplies this marker.
        if (clientLimit && !context.Items.ContainsKey(TrustedProxyKey))
        {
            return null;
        }

        var peer = context.Connection.RemoteIpAddress;
        var subject = HeadlessAuth.Subject(peer?.ToString()) ?? throw ApiException.Unavailable();
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
            var siteMaximum = (int)Math.Min((long)maximum * HeadlessAuth.SiteFactor, int.MaxValue);
            return await limiter.AcquireAsync(OidcIssuer.InstanceId, purpose + "-site", limiter.Fold(new IPAddress(bytes).ToString()), siteMaximum, Window, context.RequestAborted);
        }

        return null;
    }
}
