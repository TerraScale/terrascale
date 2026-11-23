using System.Net;
using System.Net.Sockets;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Oidc;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using static OpenIddict.Abstractions.OpenIddictConstants;

using TerraScale.DotNet;
using ZLogger;

namespace TerraScale.Authentication.Features.RateLimiting;

public sealed class TokenRateLimitMiddleware(RequestDelegate next, AttemptLimiter limiter, AuthOptions options, ILogger<TokenRateLimitMiddleware> logger, bool clientLimit)
{
    public const int ProxyFactor = 4;

    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";

    private const string DevicePath = "/" + OidcIssuer.Slug + "/oauth/device";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly object TrustedProxyKey = new();

    public static IApplicationBuilder UsePeerGuard(IApplicationBuilder app) =>
        app.UseMiddleware<TokenRateLimitMiddleware>(false);

    public static IApplicationBuilder UseClientGuard(IApplicationBuilder app) =>
        app.UseMiddleware<TokenRateLimitMiddleware>(true);

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HttpMethods.IsPost(context.Request.Method)
            || !(context.Request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(DevicePath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(DevicePath + "/", StringComparison.OrdinalIgnoreCase)
                || IsProjectTokenPath(context.Request.Path)
                || AgentIdIssuer.Guarded(context.Request.Path)
                || CustomTokenEndpoint.Matches(context.Request.Path)))
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
            logger.ZLogWarning(error.InnerException ?? error, $"Token request refused: its rate limit could not be checked");
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.RetryAfter = "2";
            refusal = CustomTokenEndpoint.Matches(context.Request.Path)
                ? TypedResults.Json(new ErrorBody(new ErrorDetail("temporarily_unavailable", "The token request could not be checked; retry shortly.")),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : TypedResults.Json(new OAuthErrorBody(OidcErrors.TemporarilyUnavailable, "The token request could not be checked; retry shortly."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (refusal is not null)
        {
            await refusal.ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    internal static bool IsProjectTokenPath(PathString path) =>
        (path.Value ?? string.Empty).Split('/') is [_, var tenant, var oauth, var token, ..] rest
        && rest.Length <= 5 && (rest.Length == 4 || rest[4].Length == 0)
        && string.Equals(oauth, "oauth", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(token, "token", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "anonymous", StringComparison.OrdinalIgnoreCase))
        && TerraScaleIds.IsValid(tenant);

    private async Task<TimeSpan?> LimitAsync(HttpContext context)
    {
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
            if (IPAddress.IsLoopback(peer))
            {
                return null;
            }

            maximum = (int)Math.Min((long)maximum * ProxyFactor, int.MaxValue);
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
            var siteMaximum = (int)Math.Min((long)maximum * HostedAuth.SiteFactor, int.MaxValue);
            return await limiter.AcquireAsync(OidcIssuer.InstanceId, purpose + "-site", limiter.Fold(new IPAddress(bytes).ToString()), siteMaximum, Window, context.RequestAborted);
        }

        return null;
    }
}
