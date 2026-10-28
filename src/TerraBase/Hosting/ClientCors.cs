namespace TerraBase.Hosting;

/// <summary>Bearer-only browser access to the public document API.</summary>
internal static class ClientCors
{
    private const string Policy = "document-api";
    private const string ProbePolicy = "public-probes";

    public static IServiceCollection AddTerraBaseClientCors(this IServiceCollection services) => services.AddCors(cors =>
    {
        cors.AddPolicy(ProbePolicy, policy => policy.AllowAnyOrigin().WithMethods("GET")
            .WithHeaders("X-TerraScale-Region-Pin")
            .WithExposedHeaders("X-TerraBase-Region", "X-Request-ID", "Retry-After"));
        cors.AddPolicy(Policy, policy => policy.AllowAnyOrigin()
            .WithMethods("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders("Authorization", "Content-Type", "If-Match", "If-None-Match", "Last-Event-ID", "TB-Shape-Resume", "X-TerraScale-Region-Pin")
            .WithExposedHeaders("ETag", "Location", "Retry-After", "TB-Shape-Cursor", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "X-TerraBase-Region", "X-TerraBase-Machine", "X-Request-ID"));
    });

    public static WebApplication UseTerraBaseClientCors(this WebApplication app)
    {
        // Preflight has no endpoint listener metadata and no bearer token. Restrict it before the listener/auth gates.
        app.UseWhen(context => Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && context.Request.Path.Value is { } path
                && (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase)
                    || path.Equals("/backupz", StringComparison.OrdinalIgnoreCase)),
            branch => branch.UseCors(ProbePolicy));
        app.UseWhen(context => Listeners.ArrivedOn(context) == TerraBaseListener.Client && IsDataPath(context.Request.Path),
            branch => branch.UseCors(Policy));
        return app;
    }

    private static bool IsDataPath(PathString path)
    {
        // Same segments as string.Split('/') without allocating: the eighth range keeps any remainder.
        var value = path.Value.AsSpan();
        Span<Range> segments = stackalloc Range[8];
        return value.Split(segments, '/') >= 7 && value[segments[0]].IsEmpty
            && !value[segments[3]].IsEmpty && !value[segments[5]].IsEmpty
            && value[segments[1]].Equals("v1", StringComparison.OrdinalIgnoreCase)
            && value[segments[2]].Equals("projects", StringComparison.OrdinalIgnoreCase)
            && value[segments[4]].Equals("databases", StringComparison.OrdinalIgnoreCase)
            && (value[segments[6]].Equals("collections", StringComparison.OrdinalIgnoreCase)
                || value[segments[6]].Equals("transactions", StringComparison.OrdinalIgnoreCase)
                || value[segments[6]].Equals("changes", StringComparison.OrdinalIgnoreCase));
    }
}
