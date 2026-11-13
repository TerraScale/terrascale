using Microsoft.Net.Http.Headers;

namespace TerraBase.Hosting;

internal static class ClientCors
{
    private const string Policy = "data-api";
    private const string ProbePolicy = "public-probes";

    public static IServiceCollection AddTerraBaseClientCors(this IServiceCollection services) => services.AddCors(cors =>
    {
        cors.AddPolicy(ProbePolicy, policy => policy.AllowAnyOrigin().WithMethods("GET")
            .WithHeaders("X-TerraScale-Region-Pin")
            .WithExposedHeaders("X-TerraBase-Region", "X-Request-ID", "Retry-After"));
        cors.AddPolicy(Policy, policy => policy.AllowAnyOrigin()
            .WithMethods("GET", "HEAD", "POST")
            .WithHeaders("Authorization", "Content-Type", "If-None-Match", "TB-Shape-Resume", "X-TerraScale-Region-Pin")
            .WithExposedHeaders("ETag", "Location", "Retry-After", "TB-Shape-Hints", "TB-Shape-Cursor", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "X-TerraBase-Region", "X-TerraBase-Machine", "X-Request-ID"));
    });

    public static WebApplication UseTerraBaseClientCors(this WebApplication app)
    {
        app.UseWhen(context => Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && context.Request.Path.Value is { } path
                && (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/backupz", StringComparison.OrdinalIgnoreCase)),
            branch => branch
                .Use((context, next) =>
                {
                    context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                    context.Response.Headers["Timing-Allow-Origin"] = "*";
                    return next(context);
                })
                .UseCors(ProbePolicy));
        app.UseWhen(context => Listeners.ArrivedOn(context) == TerraBaseListener.Client && IsDataPath(context.Request.Path),
            branch => branch.UseCors(Policy));
        return app;
    }

    private static bool IsDataPath(PathString path)
    {
        var segments = path.Value?.Split('/');
        return segments is { Length: >= 7 } && segments[0].Length == 0
            && segments[3].Length > 0 && segments[5].Length > 0
            && string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[2], "projects", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[4], "databases", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(segments[6], "sql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[6], "tables", StringComparison.OrdinalIgnoreCase));
    }
}
