using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Bounds every token POST, the platform's and each project tenant's, and every hosted <c>/ui</c> request to <see cref="Deadline"/>. The request's
/// <see cref="HttpContext.RequestAborted"/> is cancelled then; the rate limiter, OpenIddict's handlers, the stores and the
/// ts-dotnet client all observe it, so stalled storage answers 503 temporarily_unavailable with Retry-After instead of
/// holding the caller. A write the deadline abandoned may still commit afterwards. That cannot rotate twice:
/// <see cref="RedeemExactlyOnce"/> redeems a code or refresh token by a conditional write on the version the request
/// validated, so of the late commit and a retry exactly one wins and the other answers invalid_grant.
/// </summary>
public sealed class TokenEndpointDeadline(RequestDelegate next, TimeProvider clock, ILogger<TokenEndpointDeadline> logger)
{
    /// <summary>How long a token or hosted-page request may wait on storage.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";

    /// <summary>Runs the rest of the pipeline under the deadline, answering 503 when it expires first.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // OpenIddict treats casing and a single trailing slash as equivalent endpoint paths.
        var token = HttpMethods.IsPost(context.Request.Method)
            && (context.Request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)
                || TokenRateLimitMiddleware.IsProjectTokenPath(context.Request.Path));
        // Hosted pages (sign-in, sign-up, reset, device, step-up) wait on storage the same way; without a deadline a
        // stalled TerraBase held the browser for the client's 30 s request timeout per call.
        var hosted = !token && context.Request.Path.StartsWithSegments("/ui", StringComparison.OrdinalIgnoreCase);
        if (!token && !hosted)
        {
            await next(context);
            return;
        }

        var aborted = context.RequestAborted;
        using var deadline = new CancellationTokenSource(Deadline, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(aborted, deadline.Token);
        context.RequestAborted = bounded.Token;
        try
        {
            await next(context);
            return;
        }
        catch (OperationCanceledException) when (Expired())
        {
            // Storage gave up at the deadline.
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable && Expired())
        {
            // A fail-closed wrapper reported the cancelled storage call as unavailable.
        }
        finally
        {
            context.RequestAborted = aborted;
        }

        var segments = context.Request.Path.Value!.Split('/');
        var tenant = segments.Length > (token ? 1 : 2) ? segments[token ? 1 : 2] : string.Empty;
        logger.ZLogWarning($"{(token ? "Token" : "Hosted page"):@Kind} request {context.TraceIdentifier:@RequestId} for {tenant:@Tenant} did not complete within {Deadline.TotalSeconds:@Seconds} seconds; answered 503");
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "2";
        if (token)
        {
            await context.Response.WriteAsJsonAsync(new OAuthErrorBody(Errors.TemporarilyUnavailable, "The token request did not complete in time; retry it."),
                OidcJson.Default.OAuthErrorBody, cancellationToken: aborted);
        }
        else
        {
            await context.Response.WriteAsJsonAsync(ApiException.Unavailable().ToBody(), cancellationToken: aborted);
        }

        // A started response cannot become a 503; the error envelope aborts the connection instead.
        bool Expired() => deadline.IsCancellationRequested && !aborted.IsCancellationRequested && !context.Response.HasStarted;
    }
}
