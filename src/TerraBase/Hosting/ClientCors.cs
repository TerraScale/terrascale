using Microsoft.Net.Http.Headers;

namespace TerraBase.Hosting;

internal static class ClientCors
{
    private const string Policy = "data-api";

    private const string ProbePolicy = "public-probes";

    extension(IServiceCollection services)
    {
        public IServiceCollection AddTerraBaseClientCors() => services.AddCors(cors =>
        {
            cors.AddPolicy(ProbePolicy, policy => policy.AllowAnyOrigin().WithMethods("GET")
                .WithHeaders("X-TerraScale-Region-Pin")
                .WithExposedHeaders("X-TerraBase-Region", "X-Request-ID", "Retry-After"));
            cors.AddPolicy(Policy, policy => policy.AllowAnyOrigin()
                .WithMethods("GET", "HEAD", "POST", "PATCH", "DELETE")
                .WithHeaders("Authorization", "Content-Type", "If-None-Match", "TB-Shape-Resume", "X-TerraScale-Region-Pin", "TerraScale-Consistency", "TerraScale-Bookmark")
                .WithExposedHeaders("ETag", "Location", "Retry-After", "TerraScale-Bookmark", "TerraScale-Consistency", "TB-Shape-Hints", "TB-Shape-Cursor", "TB-Shape-Handle",
                    "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "X-TerraBase-Region", "X-TerraBase-Machine", "X-Request-ID"));
        });
    }

    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseClientCors()
        {
            app.UseWhen(
                context => Listeners.ArrivedOn(context) == TerraBaseListener.Client
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
            app.UseWhen(
                context => context.Request.Headers.ContainsKey(HeaderNames.Origin)
                    && Listeners.ArrivedOn(context) == TerraBaseListener.Client
                    && IsDataPath(context.Request.Path.Value.AsSpan()),
                branch => branch.UseCors(Policy));
            return app;
        }
    }

    private static bool IsDataPath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || path[0] != '/')
        {
            return false;
        }

        var rest = path[1..];
        if (!NextSegment(ref rest, out var s1) || !s1.Equals("v1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!NextSegment(ref rest, out var s2) || !s2.Equals("projects", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!NextSegment(ref rest, out var s3) || s3.IsEmpty)
        {
            return false;
        }

        if (!NextSegment(ref rest, out var s4) || !s4.Equals("databases", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!NextSegment(ref rest, out var s5) || s5.IsEmpty)
        {
            return false;
        }

        if (!NextSegment(ref rest, out var s6))
        {
            return false;
        }

        return s6.Equals("sql", StringComparison.OrdinalIgnoreCase)
            || s6.Equals("tables", StringComparison.OrdinalIgnoreCase)
            || s6.Equals("rows:batch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NextSegment(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> segment)
    {
        if (remaining.IsEmpty)
        {
            segment = default;
            return false;
        }

        var slash = remaining.IndexOf('/');
        if (slash < 0)
        {
            segment = remaining;
            remaining = default;
        }
        else
        {
            segment = remaining[..slash];
            remaining = remaining[(slash + 1)..];
        }

        return true;
    }
}
