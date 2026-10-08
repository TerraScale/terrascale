using System.Net;
using System.Net.Sockets;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Features.Headless;
using TerraScale.Auth.Features.Oidc;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Auth.Features.RateLimiting;

/// <summary>
/// Charges every token POST to the socket peer's shared minute budget before OpenIddict checks credentials.
/// IPv4-mapped addresses share the IPv4 budget; IPv6 shares a /64 and an additional /48 budget.
/// Forwarding headers and submitted client identifiers never select a bucket. Missing peers and storage failures
/// fail closed with an OAuth error.
/// </summary>
public sealed class TokenRateLimitMiddleware(RequestDelegate next, AttemptLimiter limiter, AuthOptions options)
{
    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

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
        var subject = HeadlessAuth.Subject(context.Connection.RemoteIpAddress?.ToString()) ?? throw ApiException.Unavailable();
        if (await limiter.AcquireAsync(OidcIssuer.InstanceId, "token-peer-ip", limiter.Fold(subject), options.RateLimitPerWindow, Window, context.RequestAborted) is { } wait)
        {
            return wait;
        }

        if (IPAddress.Parse(subject) is { AddressFamily: AddressFamily.InterNetworkV6 } address)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 6, bytes.Length - 6);
            // AttemptLimiter accepts Int32 capacities; avoid overflow for a large configured peer budget.
            var maximum = (int)Math.Min((long)options.RateLimitPerWindow * HeadlessAuth.SiteFactor, int.MaxValue);
            return await limiter.AcquireAsync(OidcIssuer.InstanceId, "token-peer-ip-site", limiter.Fold(new IPAddress(bytes).ToString()), maximum, Window, context.RequestAborted);
        }

        return null;
    }
}
