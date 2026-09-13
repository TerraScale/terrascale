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
    protected EndpointDefinition Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") => this.Definition.Produces<T>(statusCode, contentType);
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
    protected EndpointDefinition Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") => this.Definition.Produces<T>(statusCode, contentType);
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

        return configured.Map(routes, new Func<CancellationToken, Task<TResponse>>(this.HandleAsync), typeof(EmptyRequest), typeof(TResponse));
    }
}

/// <summary>FastEndpoints-style request endpoint that writes its response through <see cref="Send"/>.</summary>
public abstract class Endpoint<TRequest> : EndpointSenderEndpoint, IEndpoint
{
    public abstract void Configure();
    public abstract Task HandleAsync(TRequest request, CancellationToken cancellationToken);

    protected EndpointDefinition Definition => Configuration;
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
    protected EndpointDefinition Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") => Definition.Produces<T>(statusCode, contentType);
    protected EndpointDefinition Accepts<T>(params string[] contentTypes) => Definition.Accepts<T>(contentTypes);
    protected EndpointDefinition AllowAnonymous() => Definition.AllowAnonymous();
    protected EndpointDefinition RequireAuthorization(params string[] policies) => Definition.RequireAuthorization(policies);
    protected EndpointDefinition Metadata(params object[] metadata) => Definition.WithMetadata(metadata);

    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ConfigureForMapping(Configure);
        return Configuration.Map(routes, ExecuteAsync, typeof(TRequest), typeof(IResult));
    }

    private async Task<IResult> ExecuteAsync(TRequest request, CancellationToken cancellationToken)
    {
        using var scope = BeginSendScope();
        await HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return scope.Result ?? Results.NoContent();
    }
}

/// <summary>FastEndpoints-style endpoint without a request body that writes through <see cref="Send"/>.</summary>
public abstract class EndpointWithoutRequest : EndpointSenderEndpoint, IEndpoint
{
    public abstract void Configure();
    public abstract Task HandleAsync(CancellationToken cancellationToken);

    protected EndpointDefinition Definition => Configuration;
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
    protected EndpointDefinition Produces<T>(int statusCode = StatusCodes.Status200OK, string contentType = "application/json") => Definition.Produces<T>(statusCode, contentType);
    protected EndpointDefinition Accepts<T>(params string[] contentTypes) => Definition.Accepts<T>(contentTypes);
    protected EndpointDefinition AllowAnonymous() => Definition.AllowAnonymous();
    protected EndpointDefinition RequireAuthorization(params string[] policies) => Definition.RequireAuthorization(policies);
    protected EndpointDefinition Metadata(params object[] metadata) => Definition.WithMetadata(metadata);

    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ConfigureForMapping(Configure);
        return Configuration.Map(routes, ExecuteAsync, typeof(EmptyRequest), typeof(IResult));
    }

    private async Task<IResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = BeginSendScope();
        await HandleAsync(cancellationToken).ConfigureAwait(false);
        return scope.Result ?? Results.NoContent();
    }
}

/// <summary>Per-request response sender used by request-only endpoints.</summary>
public sealed class EndpointSender
{
    internal IResult? Result { get; private set; }
    public Task<IResult> OkAsync<T>(T response, CancellationToken cancellationToken = default) => Set(Results.Ok(response));
    public Task<IResult> CreatedAsync<T>(string uri, T response, CancellationToken cancellationToken = default) => Set(Results.Created(uri, response));
    public Task<IResult> AcceptedAsync<T>(string? uri, T response, CancellationToken cancellationToken = default) => Set(Results.Accepted(uri, response));
    public Task<IResult> NotFoundAsync(CancellationToken cancellationToken = default) => Set(Results.NotFound());
    public Task<IResult> NoContentAsync(CancellationToken cancellationToken = default) => Set(Results.NoContent());
    public Task<IResult> ResponseAsync(IResult response, CancellationToken cancellationToken = default) => Set(response);
    private Task<IResult> Set(IResult response) { Result = response; return Task.FromResult(response); }
}

public abstract class EndpointSenderEndpoint
{
    private readonly AsyncLocal<EndpointSender?> sender = new();
    private protected EndpointDefinition Configuration { get; } = new();
    protected EndpointSender Send => sender.Value ?? throw new InvalidOperationException("Send is only available while handling a request.");
    private protected static void ConfigureForMapping(Action configure) => configure();
    private protected SendScope BeginSendScope() { var value = new EndpointSender(); sender.Value = value; return new SendScope(sender, value); }
    private protected sealed class SendScope(AsyncLocal<EndpointSender?> slot, EndpointSender value) : IDisposable
    {
        internal IResult? Result => value.Result;
        public void Dispose() => slot.Value = null;
    }
}
