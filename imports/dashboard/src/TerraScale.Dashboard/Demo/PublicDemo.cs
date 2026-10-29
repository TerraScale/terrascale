using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraScale.Dashboard.Features.Billing;

namespace TerraScale.Dashboard.Demo;

// The public synthetic demo (DEMO=true) wraps the in-memory demo host: production error pages and
// no admin, MCP, password-reset, scenario or delete/revoke/leave routes. No TerraBase, ts-auth or Stripe.
public static partial class PublicDemo
{
    // DEMO=true is the user's explicit exception to docs/zero-configuration.md: markets and demos run this
    // build without providers; unsetting it serves the real application.
    public static bool Enabled => Environment.GetEnvironmentVariable("DEMO") == "true";

    // Serves the published application directory on the image's HTTP port (8080 in the ASP.NET image).
    public static async Task<int> RunAsync()
    {
        var port = int.Parse(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080", System.Globalization.CultureInfo.InvariantCulture);
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateHost(AppContext.BaseDirectory, port, StripeBilling.Origin, Configure);
        Console.WriteLine($"DEMO (synthetic data) on port {port}; signs in as user@example.com");
        await app.RunAsync();
        return 0;
    }

    // CreateHost passes --environment Development; Production keeps stack traces off public error responses.
    // The guard runs first in the pipeline, ahead of authentication, so it answers every caller the same way.
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Environment.EnvironmentName = Environments.Production;
        builder.Services.AddSingleton<IStartupFilter, Guard>();
    }

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
        await next();
    }

    public static bool Blocked(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        return path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/v1/admin", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/v1/auth-management", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase) ||
               // The demo UI needs only the fake Stripe checkout/portal and OIDC redirects; scenarios and probes stay test-only.
               path.StartsWith("/__test", StringComparison.OrdinalIgnoreCase) &&
               !path.Equals("/__test/checkout", StringComparison.OrdinalIgnoreCase) &&
               !path.Equals("/__test/portal", StringComparison.OrdinalIgnoreCase) &&
               !path.Equals("/__test/oidc", StringComparison.OrdinalIgnoreCase) ||
               HttpMethods.IsDelete(request.Method) ||
               !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && Destructive().IsMatch(path);
    }

    [GeneratedRegex(@"/(delete|revoke|leave|destroy|purge|transfer)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Destructive();
}
