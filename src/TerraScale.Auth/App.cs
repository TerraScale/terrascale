using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Observability;
using TerraScale.Auth.Hosting;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

namespace TerraScale.Auth;

/// <summary>Composes services, middleware and endpoints.</summary>
public static class App
{
    /// <summary>OpenAPI document title; clients key on it.</summary>
    public const string ApiTitle = "TerraScale Auth API";

    /// <summary>
    /// Builds the application for validated options. <paramref name="configure"/> lets
    /// integration tests replace infrastructure before the container is built.
    /// </summary>
    public static WebApplication Build(AuthOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.Listen));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IStorageHealth, UnavailableStorageHealth>();
        builder.Services.AddSingleton<ReadinessProbe>();

        builder.Services.AddTerraScaleEndpoints(typeof(App).Assembly, endpoints =>
        {
            endpoints.Title = ApiTitle;
            endpoints.Version = "v1";
        });
        builder.Services.AddAuthOpenApi();
        builder.AddAuthTelemetry(options);

        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseMiddleware<ErrorEnvelopeMiddleware>();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        return app;
    }
}
