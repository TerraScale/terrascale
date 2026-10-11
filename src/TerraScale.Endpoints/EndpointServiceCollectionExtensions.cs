using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.OpenApi;

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
            // Post-configure, so it survives an app that replaces TypeInfoResolver in its own configuration.
            services.PostConfigure<Microsoft.AspNetCore.Http.Json.JsonOptions>(json =>
            {
                if (!json.SerializerOptions.TypeInfoResolverChain.Contains(ProblemJsonContext.Default))
                {
                    json.SerializerOptions.TypeInfoResolverChain.Add(ProblemJsonContext.Default);
                }
            });
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

    /// <summary>Maps the OpenAPI document and Scalar UI; conventions added to the returned builder apply to both.</summary>
    public static IEndpointConventionBuilder MapTerraScaleOpenApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var options = routes.ServiceProvider.GetRequiredService<EndpointOptions>();
        var docs = routes.MapGroup(string.Empty);
        docs.MapOpenApi(options.OpenApiRoute);
        docs.MapScalarApiReference(options.ScalarRoute, scalar =>
        {
            // System fonts, no telemetry and no AI agent: the page talks to nothing but this service.
            scalar
                .WithTitle(options.Title)
                .DisableDefaultFonts()
                .DisableTelemetry()
                .DisableAgent()
                .WithOpenApiRoutePattern(options.OpenApiRoute)
                .AddDocument(options.Version, options.Title, options.OpenApiRoute, isDefault: true);
        });
        return docs;
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
        services.AddOpenApi(options.Version, openApi => openApi
            .AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = options.Title;
                document.Info.Version = options.Version;
                return Task.CompletedTask;
            })
            .AddOperationTransformer(AddRequestParametersAsync));
    }

    // ApiExplorer only sees parameters that RequestDelegateFactory describes, and endpoints map a
    // plain RequestDelegate, so route and query parameters are added here. The route comes from the
    // final pattern (MapGroup prefixes included) and body-less methods get query parameters instead
    // of a body, per operation, so an endpoint mapped to GET and POST is described correctly for both.
    private static async Task AddRequestParametersAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var description = context.Description;
        if (description.ActionDescriptor.EndpointMetadata.OfType<RequestParametersMetadata>().FirstOrDefault() is not { } metadata)
        {
            return;
        }

        // Property schemas come from the request schema so property-level converters (for example a
        // string enum converter) are described the way the binder reads them.
        var requestSchema = metadata is { RequestType: { } requestType, Properties: not null }
            ? await context.GetOrCreateSchemaAsync(requestType, null, cancellationToken).ConfigureAwait(false)
            : null;
        // Only properties the binder can fill from a string bind from route slots; any other slot
        // (no matching property, or a nested object) is described as a plain string.
        var properties = metadata.Properties ?? [];
        var route = RoutePatternFactory.Parse("/" + description.RelativePath);
        var routeBound = new HashSet<JsonPropertyInfo>();
        var routeOptional = new HashSet<JsonPropertyInfo>();
        foreach (var routeParameter in route.Parameters)
        {
            var property = Find(properties, routeParameter.Name) is { } match && RequestValues.CanBindFromString(match) ? match : null;

            // An optional segment ({id?} or with a default) may be absent, and then the binder takes
            // the value from the query string, so that property stays a query parameter too. The
            // segment (or its default) can still supply it, so that query parameter is never required.
            if (property is not null)
            {
                (routeParameter.IsOptional || routeParameter.Default is not null ? routeOptional : routeBound).Add(property);
            }

            // OpenAPI requires every path parameter to be required, even optional route segments.
            await AddAsync(routeParameter.Name, ParameterLocation.Path, property, required: true).ConfigureAwait(false);
        }

        if (metadata.Properties is null)
        {
            return;
        }

        // The default binder merges query values for every method. Body-less methods drop the body
        // when every remaining property can come from the query; nested objects and dictionaries
        // cannot, so they keep it.
        var method = description.HttpMethod ?? string.Empty;
        var bodyless = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsDelete(method) || HttpMethods.IsOptions(method);
        var remaining = properties.Where(property => !routeBound.Contains(property)).ToList();
        var bodyKept = !bodyless || remaining.Exists(property => !RequestValues.CanBindFromString(property));
        foreach (var property in remaining.Where(RequestValues.CanBindFromString))
        {
            // The binder merges body, query and route values before checking required members, so
            // with a body kept a required member may come from the body instead of the query.
            var mustSend = IsRequired(property, metadata) && !routeOptional.Contains(property);
            await AddAsync(property.Name, ParameterLocation.Query, property, mustSend && !bodyKept).ConfigureAwait(false);
        }

        if (!bodyKept)
        {
            operation.RequestBody = null;
            return;
        }

        // Routing accepts a missing body for object requests, but a required member that only the
        // body can supply (a nested object or dictionary) makes this operation's body required.
        if (operation.RequestBody is OpenApiRequestBody body
            && remaining.Exists(property => !RequestValues.CanBindFromString(property) && IsRequired(property, metadata)))
        {
            body.Required = true;
        }

        // The binder merges route and query values before checking required members, so members
        // they can supply are optional in this operation's body. The shared component stays as is.
        var supplied = routeBound.Concat(remaining.Where(RequestValues.CanBindFromString)).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (requestSchema is OpenApiSchema { Required: { } required } schema
            && required.Overlaps(supplied)
            && operation.RequestBody is OpenApiRequestBody { Content: { } content })
        {
            var relaxed = (OpenApiSchema)schema.CreateShallowCopy();
            relaxed.Required = new HashSet<string>(required.Where(name => !supplied.Contains(name)), StringComparer.Ordinal);
            foreach (var media in content.Values.OfType<OpenApiMediaType>())
            {
                media.Schema = relaxed;
            }
        }

        async Task AddAsync(string name, ParameterLocation location, JsonPropertyInfo? property, bool required)
        {
            operation.Parameters ??= [];
            // OpenAPI parameter names are case-sensitive, like case-sensitive JSON contracts (id and ID).
            if (operation.Parameters.Any(parameter => parameter.In == location && string.Equals(parameter.Name, name, StringComparison.Ordinal)))
            {
                return;
            }

            var schema = property is not null && requestSchema?.Properties?.TryGetValue(property.Name, out var propertySchema) == true
                ? propertySchema
                : await context.GetOrCreateSchemaAsync(property?.PropertyType ?? typeof(string), null, cancellationToken).ConfigureAwait(false);
            operation.Parameters.Add(new OpenApiParameter { Name = name, In = location, Required = required, Schema = schema });
        }
    }

    private static bool IsRequired(JsonPropertyInfo property, RequestParametersMetadata metadata) =>
        property.IsRequired || (metadata.Validated && HasRequiredAttribute(property));

    // Data annotation [Required] on the property or its constructor parameter, as AddValidation() checks it.
    private static bool HasRequiredAttribute(JsonPropertyInfo property) =>
        property.AttributeProvider?.IsDefined(typeof(RequiredAttribute), inherit: true) == true
        || property.AssociatedParameter?.AttributeProvider?.IsDefined(typeof(RequiredAttribute), inherit: true) == true;

    // Same resolution as the binder: exact JSON name first, then the only case-insensitive match.
    private static JsonPropertyInfo? Find(IReadOnlyList<JsonPropertyInfo> properties, string name) =>
        properties.FirstOrDefault(property => property.Name == name)
        ?? (properties.Count(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) == 1
            ? properties.First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            : null);
}
