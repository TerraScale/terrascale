using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.OpenApi;
using TerraScale.Endpoints.Binding;

namespace TerraScale.Endpoints;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
public abstract class EndpointBase : IEndpoint
{
    private EndpointDefinition? definition;
    private HttpContext? httpContext;

    private protected static readonly IAsyncEnumerable<System.ComponentModel.DataAnnotations.ValidationResult> NoValidation =
        AsyncEnumerable.Empty<System.ComponentModel.DataAnnotations.ValidationResult>();

    private protected EndpointBase()
    {
    }

    public abstract void Configure();

    protected HttpContext HttpContext =>
        httpContext ?? throw new InvalidOperationException("HttpContext is only available while handling a request.");

    protected EndpointDefinition Definition =>
        definition ?? throw new InvalidOperationException("Endpoint configuration is only available from Configure().");

    protected EndpointDefinition Get(string route) => Definition.Get(route);
    protected EndpointDefinition Post(string route) => Definition.Post(route);
    protected EndpointDefinition Put(string route) => Definition.Put(route);
    protected EndpointDefinition Patch(string route) => Definition.Patch(route);
    protected EndpointDefinition Delete(string route) => Definition.Delete(route);
    protected EndpointDefinition Verbs(params string[] methods) => Definition.Verbs(methods);
    protected EndpointDefinition Route(string route) => Definition.WithRoute(route);
    protected EndpointDefinition Name(string name) => Definition.WithName(name);
    protected EndpointDefinition Summary(string summary) => Definition.WithSummary(summary);
    protected EndpointDefinition Description(string description) => Definition.WithDescription(description);
    protected EndpointDefinition Tags(params string[] tags) => Definition.WithTags(tags);
    protected EndpointDefinition Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") =>
        Definition.Produces<T>(statusCode, contentType);
    protected EndpointDefinition Produces(int statusCode) => Definition.Produces(statusCode);
    protected EndpointDefinition ProducesHeader(int statusCode, string name, string description, JsonSchemaType type) =>
        Definition.ProducesHeader(statusCode, name, description, type);
    protected EndpointDefinition ProducesSse(int statusCode = StatusCodes.Status200OK) => Definition.ProducesSse(statusCode);
    protected EndpointDefinition Accepts<T>(params string[] contentTypes) => Definition.Accepts<T>(contentTypes);
    protected EndpointDefinition AllowAnonymous() => Definition.AllowAnonymous();
    protected EndpointDefinition RequireAuthorization(params string[] policies) => Definition.RequireAuthorization(policies);
    protected EndpointDefinition Metadata(params object[] metadata) => Definition.WithMetadata(metadata);

    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var configured = new EndpointDefinition();
        definition = configured;
        try
        {
            Configure();
        }
        finally
        {
            definition = null;
        }

        configured.EnsureMappable();
        var endpointType = GetType();
        if (routes.ServiceProvider.GetService<IServiceProviderIsService>() is { } isService && !isService.IsService(endpointType))
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpointType.FullName}' is not registered. Register it with AddTerraScaleEndpoints() before mapping.");
        }

        return MapCore(routes, configured, endpointType);
    }

    private protected abstract RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType);

    private protected static Task Reject(HttpContext context, Validation invalid) =>
        TypedResults.Problem(invalid.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);

    private protected static Task Reject(HttpContext context, UnsupportedMediaType unsupported) =>
        TypedResults.Problem(unsupported.Message, statusCode: StatusCodes.Status415UnsupportedMediaType).ExecuteAsync(context);

    private protected bool UsesDefaultBinding(Type bindingBase) =>
        GetType().GetMethod("BindAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])?.DeclaringType == bindingBase;

    private protected static TEndpoint Activate<TEndpoint>(HttpContext context, Type endpointType)
        where TEndpoint : EndpointBase
    {
        var endpoint = (TEndpoint)context.RequestServices.GetRequiredService(endpointType);
        endpoint.httpContext = context;
        return endpoint;
    }
}
