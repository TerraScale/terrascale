namespace TerraScale.Endpoints;

/// <summary>Shared configuration surface and request state for the endpoint base classes.</summary>
/// <remarks>
/// Endpoints are registered as transient services, so every request gets its own instance and
/// per-request state lives in plain fields instead of async locals.
/// </remarks>
public abstract class EndpointBase : IEndpoint
{
    private EndpointDefinition? definition;
    private HttpContext? httpContext;

    /// <summary>Returned by the default <c>ValidateAsync</c>, so the pipeline can skip it without awaiting.</summary>
    private protected static readonly IAsyncEnumerable<System.ComponentModel.DataAnnotations.ValidationResult> NoValidation =
        AsyncEnumerable.Empty<System.ComponentModel.DataAnnotations.ValidationResult>();

    /// <summary>The response chosen through <c>Send</c>, for request-only endpoints.</summary>
    internal IResult? SentResult { get; set; }

    private protected EndpointBase()
    {
    }

    /// <summary>Declares the route and endpoint metadata.</summary>
    public abstract void Configure();

    /// <summary>The request context while handling a request.</summary>
    protected HttpContext HttpContext =>
        this.httpContext ?? throw new InvalidOperationException("HttpContext is only available while handling a request.");

    /// <summary>The active fluent definition during <see cref="Configure"/>.</summary>
    protected EndpointDefinition Definition =>
        this.definition ?? throw new InvalidOperationException("Endpoint configuration is only available from Configure().");

    protected EndpointDefinition Get(string route) => this.Definition.Get(route);
    protected EndpointDefinition Post(string route) => this.Definition.Post(route);
    protected EndpointDefinition Put(string route) => this.Definition.Put(route);
    protected EndpointDefinition Patch(string route) => this.Definition.Patch(route);
    protected EndpointDefinition Delete(string route) => this.Definition.Delete(route);
    protected EndpointDefinition Verbs(params string[] methods) => this.Definition.Verbs(methods);
    protected EndpointDefinition Route(string route) => this.Definition.WithRoute(route);
    protected EndpointDefinition Name(string name) => this.Definition.WithName(name);
    protected EndpointDefinition Summary(string summary) => this.Definition.WithSummary(summary);
    protected EndpointDefinition Description(string description) => this.Definition.WithDescription(description);
    protected EndpointDefinition Tags(params string[] tags) => this.Definition.WithTags(tags);
    protected EndpointDefinition Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") =>
        this.Definition.Produces<T>(statusCode, contentType);
    protected EndpointDefinition ProducesSse(int statusCode = StatusCodes.Status200OK) => this.Definition.ProducesSse(statusCode);
    protected EndpointDefinition Accepts<T>(params string[] contentTypes) => this.Definition.Accepts<T>(contentTypes);
    protected EndpointDefinition AllowAnonymous() => this.Definition.AllowAnonymous();
    protected EndpointDefinition RequireAuthorization(params string[] policies) => this.Definition.RequireAuthorization(policies);
    protected EndpointDefinition Metadata(params object[] metadata) => this.Definition.WithMetadata(metadata);

    /// <inheritdoc />
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var configured = new EndpointDefinition();
        this.definition = configured;
        try
        {
            this.Configure();
        }
        finally
        {
            this.definition = null;
        }

        configured.EnsureMappable();
        var endpointType = this.GetType();
        if (routes.ServiceProvider.GetService<IServiceProviderIsService>() is { } isService && !isService.IsService(endpointType))
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpointType.FullName}' is not registered. Register it with AddTerraScaleEndpoints() before mapping.");
        }

        return this.MapCore(routes, configured, endpointType);
    }

    private protected abstract RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType);

    // Goes through IProblemDetailsService when the app registers one, so error bodies stay customizable.
    private protected static Task Reject(HttpContext context, BadHttpRequestException exception) =>
        TypedResults.Problem(exception.Message, statusCode: exception.StatusCode).ExecuteAsync(context);

    private protected static TEndpoint Activate<TEndpoint>(HttpContext context, Type endpointType)
        where TEndpoint : EndpointBase
    {
        // Transient registration: the request scope owns disposal of the instance.
        var endpoint = (TEndpoint)context.RequestServices.GetRequiredService(endpointType);
        endpoint.httpContext = context;
        return endpoint;
    }
}
