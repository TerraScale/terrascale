using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Endpoints;

/// <summary>Options for the TerraScale endpoint integration.</summary>
public sealed class EndpointOptions
{
    /// <summary>OpenAPI document title.</summary>
    public string Title { get; set; } = "TerraScale API";

    /// <summary>OpenAPI document version.</summary>
    public string Version { get; set; } = "v1";

    /// <summary>OpenAPI route template.</summary>
    public string OpenApiRoute { get; set; } = "/openapi/{documentName}.json";

    /// <summary>Scalar UI route.</summary>
    public string ScalarRoute { get; set; } = "/scalar";
}

/// <summary>Dependency injection and application integration helpers.</summary>
public static class EndpointServiceCollectionExtensions
{
    /// <summary>Registers a cached endpoint catalog and OpenAPI services.</summary>
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);
        var options = new EndpointOptions();
        configure?.Invoke(options);
        Validate(options);
        services.AddSingleton(options);
        services.AddSingleton(new EndpointCatalog(assemblies));
        AddOpenApi(services, options);
        return services;
    }

    /// <summary>Registers endpoints from the calling assembly.</summary>
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        Action<EndpointOptions>? configure = null) =>
        services.AddTerraScaleEndpoints(Assembly.GetCallingAssembly(), configure);

    /// <summary>Registers a precomputed endpoint list without assembly scanning.</summary>
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<Type> endpointTypes,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpointTypes);
        var options = new EndpointOptions();
        configure?.Invoke(options);
        Validate(options);
        services.AddSingleton(options);
        services.AddSingleton(new EndpointCatalog(endpointTypes, alreadyDiscovered: true));
        AddOpenApi(services, options);
        return services;
    }

    /// <summary>Registers endpoints from an assembly.</summary>
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        Assembly assembly,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return services.AddTerraScaleEndpoints([assembly], configure);
    }

    /// <summary>Maps the cached endpoint catalog.</summary>
    public static IEndpointRouteBuilder MapTerraScaleEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var catalog = routes.ServiceProvider.GetRequiredService<EndpointCatalog>();
        catalog.Map(routes, routes.ServiceProvider);
        return routes;
    }

    /// <summary>Maps the OpenAPI document and Scalar UI.</summary>
    public static WebApplication MapTerraScaleOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<EndpointOptions>();
        app.MapOpenApi(options.OpenApiRoute);
        app.MapScalarApiReference(options.ScalarRoute, scalar => scalar.WithTitle(options.Title));
        return app;
    }

    private static void Validate(EndpointOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OpenApiRoute);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ScalarRoute);
    }

    private static void AddOpenApi(IServiceCollection services, EndpointOptions options)
    {
        services.AddOpenApi(options.Version, openApi => openApi.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = options.Title;
            document.Info.Version = options.Version;
            return Task.CompletedTask;
        }));
    }
}
