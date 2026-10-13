using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The public synthetic demo (deploy/demo) wraps the test-only demo host: production error pages, a DEMO label on
// every HTML document, and no admin, MCP, password-reset, scenario or delete/revoke/leave routes.
public static partial class PublicDemo
{
    public const string Banner = "DEMO — public synthetic data. Not connected to TerraScale services; payments, admin and destructive actions are disabled.";

    // CreateHost passes --environment Development; Production keeps stack traces off public error responses.
    // The guard runs first in the pipeline, ahead of authentication, so it answers every caller the same way.
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Services.AddSingleton<IStartupFilter, Guard>();
    }

    public static void Use(WebApplication app) => app.MapGet("/__demo/banner.css", () => Results.Text(
        ".ts-demo-banner{position:sticky;top:0;z-index:2147483647;padding:.5rem 1rem;background:#b91c1c;color:#fff;font:600 .9rem/1.3 system-ui,sans-serif;text-align:center}",
        "text/css"));

    private sealed class Guard : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(Invoke);
            next(app);
        };
    }

    private static async Task Invoke(HttpContext context, Func<Task> next)
    {
        if (Blocked(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("DEMO: this action is disabled in the public synthetic demo.\n");
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) || context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.Ordinal))
        {
            await next();
            return;
        }
        var body = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try { await next(); }
        finally { context.Response.Body = body; }
        var bytes = buffer.ToArray();
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.Ordinal) == true)
        {
            bytes = Encoding.UTF8.GetBytes(Label(Encoding.UTF8.GetString(bytes)));
            context.Response.ContentLength = bytes.Length;
        }
        await body.WriteAsync(bytes, context.RequestAborted);
    }

    public static bool Blocked(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        return path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/v1/auth-management", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/reset-password", StringComparison.OrdinalIgnoreCase) ||
               // The demo UI needs only the fake Stripe checkout/portal and OIDC redirects; scenarios and probes stay test-only.
               path.StartsWith("/__test", StringComparison.OrdinalIgnoreCase) &&
               !path.Equals("/__test/checkout", StringComparison.OrdinalIgnoreCase) &&
               !path.Equals("/__test/portal", StringComparison.OrdinalIgnoreCase) &&
               !path.Equals("/__test/oidc", StringComparison.OrdinalIgnoreCase) ||
               HttpMethods.IsDelete(request.Method) ||
               !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && Destructive().IsMatch(path);
    }

    public static string Label(string html)
    {
        html = Title().Replace(html, "$1DEMO · ", 1);
        html = Head().Replace(html, "<link rel=\"stylesheet\" href=\"/__demo/banner.css\">$0", 1);
        return Body().Replace(html, "$0<div class=\"ts-demo-banner\" role=\"status\">" + Banner + "</div>", 1);
    }

    [GeneratedRegex(@"/(delete|revoke|leave|destroy|purge|transfer)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Destructive();
    [GeneratedRegex("(<title>)", RegexOptions.IgnoreCase)]
    private static partial Regex Title();
    [GeneratedRegex("</head>", RegexOptions.IgnoreCase)]
    private static partial Regex Head();
    [GeneratedRegex("<body[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Body();
}
