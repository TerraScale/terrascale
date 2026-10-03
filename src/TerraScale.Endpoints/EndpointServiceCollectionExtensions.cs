using System.Diagnostics.CodeAnalysis;
using System.Reflection;

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
/// <remarks>
/// Projects referencing the package also get a generated, reflection-free
/// <c>AddTerraScaleEndpoints(Action&lt;EndpointOptions&gt;?)</c> overload that registers every endpoint in the project.
/// </remarks>
public static class EndpointServiceCollectionExtensions
{
    /// <summary>Registers endpoints and OpenAPI services. Calling it again adds more endpoints to the same catalog.</summary>
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<EndpointRegistration> endpoints,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoints);

        if (FindSingleton<EndpointCatalog>(services) is { } catalog && FindSingleton<EndpointOptions>(services) is { } options)
        {
            var documentName = options.Version;
            configure?.Invoke(options);
            Validate(options);
            if (!string.Equals(documentName, options.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The OpenAPI document version cannot change after the first AddTerraScaleEndpoints call.");
            }
        }
        else
        {
            options = new EndpointOptions();
            configure?.Invoke(options);
            Validate(options);
            catalog = new EndpointCatalog();
            services.AddSingleton(options);
            services.AddSingleton(catalog);
            AddOpenApi(services, options);
        }

        catalog.Add(services, endpoints);
        return services;
    }

    /// <summary>Registers endpoints from explicit types using reflection.</summary>
    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<Type> endpointTypes,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpointTypes);
        return services.AddTerraScaleEndpoints(endpointTypes.Select(EndpointRegistration.FromType).ToArray(), configure);
    }

    /// <summary>Registers endpoints by scanning assemblies with reflection.</summary>
    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return services.AddTerraScaleEndpoints(assemblies.Distinct().SelectMany(EndpointDiscovery.Discover), configure);
    }

    /// <summary>Registers endpoints by scanning an assembly with reflection.</summary>
    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        Assembly assembly,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return services.AddTerraScaleEndpoints([assembly], configure);
    }

    /// <summary>Maps the registered endpoints.</summary>
    public static IEndpointRouteBuilder MapTerraScaleEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var catalog = routes.ServiceProvider.GetService<EndpointCatalog>()
            ?? throw new InvalidOperationException("Call AddTerraScaleEndpoints() before MapTerraScaleEndpoints().");
        catalog.Map(routes, routes.ServiceProvider);
        return routes;
    }

    /// <summary>Maps the OpenAPI document and Scalar UI.</summary>
    public static WebApplication MapTerraScaleOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<EndpointOptions>();
        app.MapOpenApi(options.OpenApiRoute);
        app.MapScalarApiReference(options.ScalarRoute, scalar =>
        {
            scalar
                .WithTitle(options.Title)
                .WithOpenApiRoutePattern(options.OpenApiRoute)
                .AddDocument(options.Version, options.Title, options.OpenApiRoute, isDefault: true);
        });
        return app;
    }

    private static T? FindSingleton<T>(IServiceCollection services)
        where T : class
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i] is { ServiceType: var type, IsKeyedService: false, ImplementationInstance: T instance } && type == typeof(T))
            {
                return instance;
            }
        }

        return null;
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
