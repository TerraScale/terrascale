namespace TerraScale.Endpoints;

/// <summary>FastEndpoints-style base for one request/response endpoint.</summary>
/// <typeparam name="TRequest">The bound request model.</typeparam>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class Endpoint<TRequest, TResponse> : IEndpoint
{
    private EndpointDefinition? definition;

    /// <summary>Declares the route and endpoint metadata.</summary>
    public abstract void Configure();

    /// <summary>Handles one request.</summary>
    public abstract Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);

    /// <summary>The active fluent definition during <see cref="Configure"/>.</summary>
    protected EndpointDefinition Definition => this.definition ?? throw new InvalidOperationException("Endpoint configuration is only available from Configure().");

    protected void Get(string route) => this.Definition.Get(route);
    protected void Post(string route) => this.Definition.Post(route);
    protected void Put(string route) => this.Definition.Put(route);
    protected void Patch(string route) => this.Definition.Patch(route);
    protected void Delete(string route) => this.Definition.Delete(route);
    protected void Name(string name) => this.Definition.WithName(name);
    protected void Summary(string summary) => this.Definition.WithSummary(summary);
    protected void Description(string description) => this.Definition.WithDescription(description);
    protected void Tags(params string[] tags) => this.Definition.WithTags(tags);
    protected void Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") => this.Definition.Produces<T>(statusCode, contentType);
    protected void Accepts<T>(params string[] contentTypes) => this.Definition.Accepts<T>(contentTypes);
    protected void AllowAnonymous() => this.Definition.AllowAnonymous();
    protected void RequireAuthorization(params string[] policies) => this.Definition.RequireAuthorization(policies);
    protected void Metadata(params object[] metadata) => this.Definition.WithMetadata(metadata);

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

        return configured.Map(routes, new Func<TRequest, CancellationToken, Task<TResponse>>(this.HandleAsync), typeof(TRequest), typeof(TResponse));
    }
}

/// <summary>FastEndpoints-style base for an endpoint without a request body.</summary>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class EndpointWithoutRequest<TResponse> : IEndpoint
{
    private EndpointDefinition? definition;

    /// <summary>Declares the route and endpoint metadata.</summary>
    public abstract void Configure();

    /// <summary>Handles one request.</summary>
    public abstract Task<TResponse> HandleAsync(CancellationToken cancellationToken);

    /// <summary>The active fluent definition during <see cref="Configure"/>.</summary>
    protected EndpointDefinition Definition => this.definition ?? throw new InvalidOperationException("Endpoint configuration is only available from Configure().");

    protected void Get(string route) => this.Definition.Get(route);
    protected void Post(string route) => this.Definition.Post(route);
    protected void Put(string route) => this.Definition.Put(route);
    protected void Patch(string route) => this.Definition.Patch(route);
    protected void Delete(string route) => this.Definition.Delete(route);
    protected void Name(string name) => this.Definition.WithName(name);
    protected void Summary(string summary) => this.Definition.WithSummary(summary);
    protected void Description(string description) => this.Definition.WithDescription(description);
    protected void Tags(params string[] tags) => this.Definition.WithTags(tags);
    protected void Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") => this.Definition.Produces<T>(statusCode, contentType);
    protected void Accepts<T>(params string[] contentTypes) => this.Definition.Accepts<T>(contentTypes);
    protected void AllowAnonymous() => this.Definition.AllowAnonymous();
    protected void RequireAuthorization(params string[] policies) => this.Definition.RequireAuthorization(policies);
    protected void Metadata(params object[] metadata) => this.Definition.WithMetadata(metadata);

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

        return configured.Map(routes, new Func<CancellationToken, Task<TResponse>>(this.HandleAsync), typeof(EmptyRequest), typeof(TResponse));
    }
}
