using System.Diagnostics;
using System.Globalization;

namespace TerraScale.Dashboard;

/// <summary>Reports server time up to response headers without buffering HTML
/// or SSE. Full render duration cannot truthfully be measured in a header
/// until rendering ends, so streaming rendering remains unbuffered.</summary>
public sealed class PagePerformanceMiddleware(RequestDelegate next, DashboardSecurityOptions security)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Reject speculative requests before session validation or persistence.
        if (NavigationPolicy.IsSpeculation(context.Request)
            && (!security.DemoMode || !HttpMethods.IsGet(context.Request.Method)
                || context.Request.QueryString.HasValue
                || !NavigationPolicy.IsPrefetchPath(context.Request.Path.Value ?? "")
                || context.Request.Headers["Sec-Purpose"].ToString().Contains("prerender", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.Headers.CacheControl = "no-store";
            return;
        }

        var start = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append("Server-Timing",
                $"headers;dur={Stopwatch.GetElapsedTime(start).TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)}");
            // Never place account HTML in a shared cache, nor reuse it after a
            // mutation/context switch. Assets have an independent public policy.
            context.Response.Headers.CacheControl = "private, no-store";
            if (security.DemoMode && !HttpMethods.IsGet(context.Request.Method)
                && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
            {
                // Clear only speculative HTML after login, logout and mutations.
                // Keep immutable assets cached. Chromium 138+ supports this
                // directive; the client only installs rules on that version floor.
                context.Response.Headers["Clear-Site-Data"] = "\"prefetchCache\", \"prerenderCache\"";
            }
            return Task.CompletedTask;
        });
        await next(context);
    }
}
