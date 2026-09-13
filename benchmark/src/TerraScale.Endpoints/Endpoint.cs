namespace TerraScale.Endpoints;

/// <summary>Base class for one request/response endpoint.</summary>
/// <typeparam name="TRequest">The request body or bound request model.</typeparam>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class Endpoint<TRequest, TResponse> : IEndpoint
{
    /// <summary>The route pattern owned by this endpoint.</summary>
    public abstract string Route { get; }

    /// <summary>The HTTP verb. Request endpoints default to POST.</summary>
    public virtual string HttpMethod => Microsoft.AspNetCore.Http.HttpMethods.Post;

    /// <summary>The stable OpenAPI operation name.</summary>
    public virtual string? Name => null;

    /// <summary>The OpenAPI summary.</summary>
    public virtual string? Summary => null;

    /// <summary>The OpenAPI description.</summary>
    public virtual string? Description => null;

    /// <summary>The OpenAPI tags.</summary>
    public virtual IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>Configures this endpoint. Override for fluent or advanced metadata.</summary>
    /// <param name="definition">The fluent endpoint definition.</param>
    public virtual void Configure(EndpointDefinition definition) => this.ConfigureDefaults(definition);

    /// <summary>Executes this endpoint.</summary>
    /// <param name="request">The bound request.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    public abstract Task<TResponse> ExecuteAsync(TRequest request, CancellationToken cancellationToken);

    /// <inheritdoc />
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var definition = new EndpointDefinition();
        this.Configure(definition);
        return definition.Map(
            routes,
            new Func<TRequest, CancellationToken, Task<TResponse>>(this.ExecuteAsync),
            typeof(TRequest),
            typeof(TResponse));
    }

    private void ConfigureDefaults(EndpointDefinition definition)
    {
        definition.Method(this.HttpMethod, this.Route);
        if (this.Name is not null) definition.WithName(this.Name);
        if (this.Summary is not null) definition.WithSummary(this.Summary);
        if (this.Description is not null) definition.WithDescription(this.Description);
        if (this.Tags.Count > 0) definition.WithTags(this.Tags.ToArray());
    }
}

/// <summary>Base class for an endpoint that has no request body.</summary>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class EndpointWithoutRequest<TResponse> : IEndpoint
{
    /// <summary>The route pattern owned by this endpoint.</summary>
    public abstract string Route { get; }

    /// <summary>The HTTP verb. No-request endpoints default to GET.</summary>
    public virtual string HttpMethod => Microsoft.AspNetCore.Http.HttpMethods.Get;

    /// <summary>The stable OpenAPI operation name.</summary>
    public virtual string? Name => null;

    /// <summary>The OpenAPI summary.</summary>
    public virtual string? Summary => null;

    /// <summary>The OpenAPI description.</summary>
    public virtual string? Description => null;

    /// <summary>The OpenAPI tags.</summary>
    public virtual IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>Configures this endpoint. Override for fluent or advanced metadata.</summary>
    /// <param name="definition">The fluent endpoint definition.</param>
    public virtual void Configure(EndpointDefinition definition) => this.ConfigureDefaults(definition);

    /// <summary>Executes this endpoint.</summary>
    /// <param name="cancellationToken">The request cancellation token.</param>
    public abstract Task<TResponse> ExecuteAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var definition = new EndpointDefinition();
        this.Configure(definition);
        return definition.Map(
            routes,
            new Func<CancellationToken, Task<TResponse>>(this.ExecuteAsync),
            typeof(EmptyRequest),
            typeof(TResponse));
    }

    private void ConfigureDefaults(EndpointDefinition definition)
    {
        definition.Method(this.HttpMethod, this.Route);
        if (this.Name is not null) definition.WithName(this.Name);
        if (this.Summary is not null) definition.WithSummary(this.Summary);
        if (this.Description is not null) definition.WithDescription(this.Description);
        if (this.Tags.Count > 0) definition.WithTags(this.Tags.ToArray());
    }
}
