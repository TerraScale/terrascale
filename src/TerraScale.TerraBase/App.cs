using System.Net;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase;

/// <summary>Composes services, middleware and endpoints.</summary>
public static class App
{
    /// <summary>OpenAPI document title.</summary>
    public const string ApiTitle = "TerraBase API";

    /// <summary>
    /// Builds the application for validated options. <paramref name="configure"/> lets
    /// integration tests replace infrastructure before the container is built.
    /// </summary>
    public static WebApplication Build(TerraBaseOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // The administrative listener is loopback-only by construction; it is never configurable.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.AdminPort));
        // Per-request framework logs are metrics' job; keep the log stream for events.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, TerraBaseJsonContext.Default));

        builder.Services.AddTerraScaleEndpoints(endpoints =>
        {
            endpoints.Title = ApiTitle;
            endpoints.Version = "v1";
        });
        builder.Services.AddTerraBaseTelemetry();

        configure?.Invoke(builder);

        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        app.MapTerraBaseMetrics();
        return app;
    }
}
