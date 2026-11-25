using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Storage;
using ZLogger;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Features.Oidc;

public sealed class TokenEndpointDeadline(RequestDelegate next, TimeProvider clock, ILogger<TokenEndpointDeadline> logger)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private const string TokenPath = "/" + OidcIssuer.Slug + "/oauth/token";

    private const string DevicePath = "/" + OidcIssuer.Slug + "/oauth/device";

    public static bool IsOAuthTokenRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HttpMethods.IsPost(request.Method)
            && (request.Path.Equals(TokenPath, StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals(TokenPath + "/", StringComparison.OrdinalIgnoreCase)
                || TokenRateLimitMiddleware.IsProjectTokenPath(request.Path)
                || request.Path.Value is { } path && path.TrimEnd('/').Equals(AgentIdIssuer.TokenPath, StringComparison.OrdinalIgnoreCase));
    }

    public static bool UsesOAuthErrors(HttpRequest request, int status)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsOAuthTokenRequest(request)
            || HttpMethods.IsPost(request.Method) && request.Path.Value is { } device && device.TrimEnd('/').Equals(DevicePath, StringComparison.OrdinalIgnoreCase)
            || request.Path.Value is { } authorize && authorize.TrimEnd('/').Equals(AgentIdIssuer.AuthorizePath, StringComparison.OrdinalIgnoreCase)
            || status == StatusCodes.Status429TooManyRequests && CustomTokenEndpoint.Matches(request.Path);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var token = IsOAuthTokenRequest(context.Request);
        var guarded = token || AgentIdIssuer.Guarded(context.Request.Path);
        var hosted = !guarded && context.Request.Path.StartsWithSegments("/ui", StringComparison.OrdinalIgnoreCase);
        if (!guarded && !hosted)
        {
            await next(context);
            return;
        }

        var aborted = context.RequestAborted;
        var writes = AuthData.TrackWrites();
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

        string[] segments = context.Request.Path.Value is { } path ? path.Split('/') : [];
        var tenant = segments.Length > (guarded ? 1 : 2) ? segments[guarded ? 1 : 2] : string.Empty;
        logger.ZLogWarning($"{(guarded ? "Token" : "Hosted page"):@Kind} request {context.TraceIdentifier:@RequestId} for {tenant:@Tenant} did not complete within {Deadline.TotalSeconds:@Seconds} seconds; answered 503");
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        var answer = writes.MayHaveCommitted ? AuthData.OutcomeUnknown(null) : ApiException.Unavailable();
        if (!writes.MayHaveCommitted)
        {
            context.Response.Headers.RetryAfter = "2";
        }

        if (UsesOAuthErrors(context.Request, StatusCodes.Status503ServiceUnavailable))
        {
            await context.Response.WriteAsJsonAsync(new OAuthErrorBody(OidcErrors.TemporarilyUnavailable,
                writes.MayHaveCommitted ? "The token request did not complete in time and may have taken effect; a repeated grant can answer invalid_grant." : "The token request did not complete in time; retry it."),
                OidcJson.Default.OAuthErrorBody, cancellationToken: aborted);
        }
        else if (SignInFlow.HostedPage(context))
        {
            await SignInFlow.Transient(context).ExecuteAsync(context);
        }
        else
        {
            await context.Response.WriteAsJsonAsync(answer.ToBody(), cancellationToken: aborted);
        }

        bool Expired() => deadline.IsCancellationRequested && !aborted.IsCancellationRequested && !context.Response.HasStarted;
    }
}
