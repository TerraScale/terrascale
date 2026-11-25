using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.OpenApi;

namespace TerraScale.Endpoints;

public sealed class EndpointOptions
{
    public string Title { get; set; } = "TerraScale API";

    public string Version { get; set; } = "v1";

    public string OpenApiRoute { get; set; } = "/openapi/{documentName}.json";

    public string ScalarRoute { get; set; } = "/scalar";

    internal EndpointResponseRegistry Responses { get; } = new();

    public EndpointOptions MapResponse<TCase>(
        Func<HttpContext, TCase, IResult> map,
        int statusCode,
        Type? bodyType = null,
        string? contentType = null)
    {
        Responses.Map(map, statusCode, bodyType, contentType);
        return this;
    }

    public EndpointOptions MapResponse<TCase>(
        Func<TCase, IResult> map,
        int statusCode,
        Type? bodyType = null,
        string? contentType = null)
    {
        Responses.Map(map, statusCode, bodyType, contentType);
        return this;
    }
}

public static class EndpointServiceCollectionExtensions
{
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

    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<Type> endpointTypes,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpointTypes);
        return services.AddTerraScaleEndpoints(endpointTypes.Select(EndpointRegistration.FromType).ToArray(), configure);
    }

    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        IEnumerable<Assembly> assemblies,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return services.AddTerraScaleEndpoints(assemblies.Distinct().SelectMany(EndpointDiscovery.Discover), configure);
    }

    [RequiresUnreferencedCode(EndpointDiscovery.ReflectionMessage)]
    public static IServiceCollection AddTerraScaleEndpoints(
        this IServiceCollection services,
        Assembly assembly,
        Action<EndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return services.AddTerraScaleEndpoints([assembly], configure);
    }

    public static IEndpointRouteBuilder MapTerraScaleEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var catalog = routes.ServiceProvider.GetService<EndpointCatalog>()
            ?? throw new InvalidOperationException("Call AddTerraScaleEndpoints() before MapTerraScaleEndpoints().");
        catalog.Map(routes, routes.ServiceProvider);
        return routes;
    }

    public static IEndpointConventionBuilder MapTerraScaleOpenApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var options = routes.ServiceProvider.GetRequiredService<EndpointOptions>();
        var docs = routes.MapGroup(string.Empty);
        docs.MapOpenApi(options.OpenApiRoute);
        docs.MapScalarApiReference(options.ScalarRoute, scalar =>
        {
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
            .AddOperationTransformer(AddRequestParametersAsync)
            .AddOperationTransformer((operation, context, _) =>
            {
                if (operation.Responses is null)
                {
                    return Task.CompletedTask;
                }

                foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                    response.Headers[RequestIdentity.Header] = new OpenApiHeader
                    {
                        Description = "Customer request correlation ULID; retained across authenticated first-party hops. Request logs are tenant-scoped and best effort.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 26, MaxLength = 26 },
                    };
                }

                foreach (var header in context.Description.ActionDescriptor.EndpointMetadata.OfType<ResponseHeaderMetadata>())
                {
                    if (operation.Responses.TryGetValue(header.StatusCode.ToString(CultureInfo.InvariantCulture), out var declared) && declared is OpenApiResponse response)
                    {
                        (response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal))[header.Name] = new OpenApiHeader
                        {
                            Description = header.Description,
                            Required = true,
                            Schema = new OpenApiSchema { Type = header.Type },
                        };
                    }
                }

                return Task.CompletedTask;
            }));
    }

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

        var requestSchema = metadata is { RequestType: { } requestType, Properties: not null }
            ? await context.GetOrCreateSchemaAsync(requestType, null, cancellationToken).ConfigureAwait(false)
            : null;
        var properties = metadata.Properties ?? [];
        var route = RoutePatternFactory.Parse("/" + description.RelativePath);
        var routeBound = new HashSet<JsonPropertyInfo>();
        var routeOptional = new HashSet<JsonPropertyInfo>();
        foreach (var routeParameter in route.Parameters)
        {
            var property = Find(properties, routeParameter.Name) is { } match && RequestValues.CanBindFromString(match) ? match : null;
            if (property is not null)
            {
                (routeParameter.IsOptional || routeParameter.Default is not null ? routeOptional : routeBound).Add(property);
            }

            await AddAsync(routeParameter.Name, ParameterLocation.Path, property, required: true).ConfigureAwait(false);
        }

        if (metadata.Properties is null)
        {
            return;
        }

        var bodyless = description.HttpMethod is { } method
            && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsDelete(method) || HttpMethods.IsOptions(method));
        var remaining = properties.Where(property => !routeBound.Contains(property)).ToList();
        var bodyKept = !bodyless || remaining.Exists(property => !RequestValues.CanBindFromString(property));
        foreach (var property in remaining.Where(RequestValues.CanBindFromString))
        {
            var mustSend = IsRequired(property, metadata) && !routeOptional.Contains(property);
            await AddAsync(property.Name, ParameterLocation.Query, property, mustSend && !bodyKept).ConfigureAwait(false);
        }

        if (!bodyKept)
        {
            operation.RequestBody = null;
            return;
        }

        if (operation.RequestBody is OpenApiRequestBody body
            && remaining.Exists(property => !RequestValues.CanBindFromString(property) && IsRequired(property, metadata)))
        {
            body.Required = true;
        }

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

    private static bool HasRequiredAttribute(JsonPropertyInfo property) =>
        property.AttributeProvider?.IsDefined(typeof(RequiredAttribute), inherit: true) == true
        || property.AssociatedParameter?.AttributeProvider?.IsDefined(typeof(RequiredAttribute), inherit: true) == true;

    private static JsonPropertyInfo? Find(IReadOnlyList<JsonPropertyInfo> properties, string name) =>
        properties.FirstOrDefault(property => property.Name == name)
        ?? (properties.Count(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) == 1
            ? properties.First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            : null);
}
