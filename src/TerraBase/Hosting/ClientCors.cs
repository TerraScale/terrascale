namespace TerraBase.Hosting;

/// <summary>Bearer-only browser access to the public document API.</summary>
internal static class ClientCors
{
    private const string Policy = "document-api";

    public static IServiceCollection AddTerraBaseClientCors(this IServiceCollection services) => services.AddCors(cors =>
        cors.AddPolicy(Policy, policy => policy.AllowAnyOrigin()
            .WithMethods("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders("Authorization", "Content-Type", "If-Match", "If-None-Match", "Last-Event-ID", "TB-Shape-Resume", "X-TerraScale-Region-Pin")
            .WithExposedHeaders("ETag", "Location", "Retry-After", "TB-Shape-Cursor", "TB-Shape-Handle", "TB-Shape-Next-Offset", "TB-Shape-Up-To-Date", "X-TerraBase-Region", "X-TerraBase-Machine")));

    public static WebApplication UseTerraBaseClientCors(this WebApplication app)
    {
        // Preflight has no endpoint listener metadata and no bearer token. Restrict it before the listener/auth gates.
        app.UseWhen(context => Listeners.ArrivedOn(context) == TerraBaseListener.Client && IsDataPath(context.Request.Path),
            branch => branch.UseCors(Policy));
        return app;
    }

    private static bool IsDataPath(PathString path)
    {
        var segments = path.Value?.Split('/');
        return segments is ["", "v1", "projects", { Length: > 0 }, "databases", { Length: > 0 }, ..]
            && segments.Length >= 7
            && segments[6] is "collections" or "transactions" or "changes";
    }
}
