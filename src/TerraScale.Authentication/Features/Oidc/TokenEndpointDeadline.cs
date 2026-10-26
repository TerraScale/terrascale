using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.RateLimiting;
using ZLogger;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

/// <summary>
/// Bounds every token POST, the platform's and each project tenant's, to <see cref="Deadline"/>. The request's
/// <see cref="HttpContext.RequestAborted"/> is cancelled then; the rate limiter, OpenIddict's handlers, the stores and the
/// ts-dotnet client all observe it, so stalled storage answers 503 temporarily_unavailable with Retry-After instead of
/// holding the caller. A write the deadline abandoned may still commit afterwards. That cannot rotate twice:
/// <see cref="RedeemExactlyOnce"/> redeems a code or refresh token by a conditional write on the version the request
/// validated, so of the late commit and a retry exactly one wins and the other answers invalid_grant.
/// </summary>
public sealed class TokenEndpointDeadline(RequestDelegate next, TimeProvider clock, ILogger<TokenEndpointDeadline> logger)
{
    /// <summary>How long a token request may wait on storage.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";

    /// <summary>Runs the rest of the pipeline under the deadline, answering 503 when it expires first.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // OpenIddict treats casing and a single trailing slash as equivalent endpoint paths.
        if (!HttpMethods.IsPost(context.Request.Method)
            || !(context.Request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)
                || TokenRateLimitMiddleware.IsProjectTokenPath(context.Request.Path)))
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

        logger.ZLogWarning($"Token request {context.TraceIdentifier:@RequestId} for tenant {context.Request.Path.Value!.Split('/')[1]:@Tenant} did not complete within {Deadline.TotalSeconds:@Seconds} seconds; answered 503");
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "2";
        await context.Response.WriteAsJsonAsync(new OAuthErrorBody(Errors.TemporarilyUnavailable, "The token request did not complete in time; retry it."),
            OidcJson.Default.OAuthErrorBody, cancellationToken: aborted);

        // A started response cannot become a 503; the error envelope aborts the connection instead.
        bool Expired() => deadline.IsCancellationRequested && !aborted.IsCancellationRequested && !context.Response.HasStarted;
    }
}
