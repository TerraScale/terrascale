using Microsoft.AspNetCore.Http.Features;
using TerraScale.Platform.Features.Functions;

namespace TerraScale.Platform.Hosting;

public sealed class RequestAdmission(RequestDelegate next, PlatformAccess access, PlatformOptions options)
{
    private static readonly PlatformUnauthorized MissingCredential = new("a bearer token, API key or peer credential is required", "invalid_key");

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<FunctionBodyLimit>();
        if ((limit is not null || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: true })
            && CallerCredential.From(context.Request, options.PeerSecret) is null)
        {
            return ErrorResults.From(MissingCredential).ExecuteAsync(context);
        }

        return limit is null ? next(context) : AdmitFunctionBodyAsync(context, limit);
    }

    private async Task AdmitFunctionBodyAsync(HttpContext context, FunctionBodyLimit limit)
    {
        var values = context.Request.RouteValues;
        var outcome = await FunctionTargets.ResolveAsync(
            access,
            context,
            RouteValue(values, "project"),
            RouteValue(values, "database"),
            RouteValue(values, "function"),
            limit.Scopes,
            context.RequestAborted);
        await (outcome switch
        {
            FunctionTarget => AdmitAsync(context, limit),
            PlatformInvalidRequest invalid => ErrorResults.From(invalid).ExecuteAsync(context),
            PlatformUnauthorized unauthorized => ErrorResults.From(unauthorized).ExecuteAsync(context),
            PlatformForbidden forbidden => ErrorResults.From(forbidden).ExecuteAsync(context),
            PlatformNotFound missing => ErrorResults.From(missing).ExecuteAsync(context),
            PlatformUnavailable unavailable => ErrorResults.From(unavailable).ExecuteAsync(context),
        });
    }

    private Task AdmitAsync(HttpContext context, FunctionBodyLimit limit)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit.Bytes;
        }

        return next(context);
    }

    private static string RouteValue(RouteValueDictionary values, string name) =>
        values[name] as string ?? throw new InvalidOperationException($"the route has no {name} value");
}
