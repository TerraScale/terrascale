using TerraScale.Dashboard.Features.Shared.Pages.NotFound;

namespace TerraScale.Dashboard;

/// <summary>Renders the public 404 page for browser navigations while preserving data responses.</summary>
public sealed class NotFoundMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);
        if (!ShouldRenderPage(context))
        {
            return;
        }

        context.Response.ContentLength = null;
        var slice = NotFoundSlice.Create(new NotFoundPageModel());
        slice.StatusCode = StatusCodes.Status404NotFound;
        await ((IResult)slice).ExecuteAsync(context);
    }

    private static bool ShouldRenderPage(HttpContext context)
    {
        var request = context.Request;
        if (context.Response.StatusCode != StatusCodes.Status404NotFound || context.Response.HasStarted
            || !HttpMethods.IsGet(request.Method) || request.Path.StartsWithSegments("/api"))
        {
            return false;
        }

        var accept = request.Headers.Accept.ToString();
        // Datastar sends a mixed Accept header that includes text/html. Its
        // data markers take priority over the browser navigation marker.
        return !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            && !request.Headers.ContainsKey("Datastar-Request");
    }
}
