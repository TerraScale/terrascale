namespace TerraScale.Endpoints;

/// <summary>Base class for one request/response endpoint.</summary>
/// <typeparam name="TRequest">The request body or bound request model.</typeparam>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class Endpoint<TRequest, TResponse> : IEndpoint
{
    /// <summary>Configures this endpoint's route and metadata.</summary>
    /// <param name="definition">The fluent endpoint definition.</param>
    public abstract void Configure(EndpointDefinition definition);

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
}

/// <summary>Base class for an endpoint that has no request body.</summary>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class EndpointWithoutRequest<TResponse> : IEndpoint
{
    /// <summary>Configures this endpoint's route and metadata.</summary>
    /// <param name="definition">The fluent endpoint definition.</param>
    public abstract void Configure(EndpointDefinition definition);

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
}
