using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using ZLogger;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class TokenEndpointDeadline(RequestDelegate next, TimeProvider clock, ILogger<TokenEndpointDeadline> logger)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = HttpMethods.IsPost(context.Request.Method)
            && (context.Request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)
                || TokenRateLimitMiddleware.IsProjectTokenPath(context.Request.Path))
            || AgentIdIssuer.Guarded(context.Request.Path);
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
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status503ServiceUnavailable && Expired())
        {
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
            await context.Response.WriteAsJsonAsync(new OAuthErrorBody(OidcErrors.TemporarilyUnavailable, "The token request did not complete in time; retry it."),
                OidcJson.Default.OAuthErrorBody, cancellationToken: aborted);
        }
        else if (SignInFlow.HostedPage(context))
        {
            await SignInFlow.Transient(context).ExecuteAsync(context);
        }
        else
        {
            await context.Response.WriteAsJsonAsync(ApiException.Unavailable().ToBody(), cancellationToken: aborted);
        }

        bool Expired() => deadline.IsCancellationRequested && !aborted.IsCancellationRequested && !context.Response.HasStarted;
    }
}
