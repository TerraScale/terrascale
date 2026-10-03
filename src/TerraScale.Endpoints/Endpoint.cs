using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Endpoints;

/// <summary>FastEndpoints-style base for one request/response endpoint.</summary>
/// <typeparam name="TRequest">The request model, bound from the JSON body unless <see cref="BindAsync"/> is overridden.</typeparam>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class Endpoint<TRequest, TResponse> : EndpointBase
{
    private static readonly MethodInfo Signature = ((Func<TRequest, CancellationToken, Task<TResponse>>)Describe).Method;

    private JsonTypeInfo<TRequest>? requestJson;

    /// <summary>Handles one request.</summary>
    /// <remarks>Return <c>ValueTask.FromResult(value)</c> (or write an <c>async</c> method) to avoid a task allocation per request.</remarks>
    public abstract ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);

    /// <summary>Binds the request model. Reads the JSON body by default; throw <see cref="BadHttpRequestException"/> to reject.</summary>
    protected virtual ValueTask<TRequest> BindAsync(CancellationToken cancellationToken) =>
        EndpointJson.ReadBodyAsync(this.HttpContext, this.requestJson!, cancellationToken);

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var requestJson = EndpointJson.GetRequestTypeInfo<TRequest>(routes.ServiceProvider);
        var responseJson = EndpointJson.GetResponseTypeInfo<TResponse>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<Endpoint<TRequest, TResponse>>(context, endpointType);
                endpoint.requestJson = requestJson;
                var cancellationToken = context.RequestAborted;
                ValueTask<TRequest> binding;
                try
                {
                    binding = endpoint.BindAsync(cancellationToken);
                }
                catch (BadHttpRequestException exception)
                {
                    return Reject(context, exception);
                }

                return binding.IsCompletedSuccessfully
                    ? EndpointJson.RespondAsync(context, endpoint.HandleAsync(binding.Result, cancellationToken), responseJson)
                    : CompleteAsync(context, endpoint, binding, responseJson, cancellationToken);
            },
            Signature,
            typeof(TRequest),
            typeof(TResponse));
    }

    private static async Task CompleteAsync(
        HttpContext context,
        Endpoint<TRequest, TResponse> endpoint,
        ValueTask<TRequest> binding,
        JsonTypeInfo<TResponse>? responseJson,
        CancellationToken cancellationToken)
    {
        TRequest request;
        try
        {
            request = await binding.ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            await Reject(context, exception).ConfigureAwait(false);
            return;
        }

        await EndpointJson.RespondAsync(context, endpoint.HandleAsync(request, cancellationToken), responseJson).ConfigureAwait(false);
    }

    private static Task<TResponse> Describe(TRequest request, CancellationToken cancellationToken) => throw new UnreachableException();
}

/// <summary>FastEndpoints-style base for an endpoint without a request body.</summary>
/// <typeparam name="TResponse">The response model or <see cref="IResult"/>.</typeparam>
public abstract class EndpointWithoutRequest<TResponse> : EndpointBase
{
    private static readonly MethodInfo Signature = ((Func<CancellationToken, Task<TResponse>>)Describe).Method;

    /// <summary>Handles one request.</summary>
    /// <remarks>Return <c>ValueTask.FromResult(value)</c> (or write an <c>async</c> method) to avoid a task allocation per request.</remarks>
    public abstract ValueTask<TResponse> HandleAsync(CancellationToken cancellationToken);

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var responseJson = EndpointJson.GetResponseTypeInfo<TResponse>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<EndpointWithoutRequest<TResponse>>(context, endpointType);
                return EndpointJson.RespondAsync(context, endpoint.HandleAsync(context.RequestAborted), responseJson);
            },
            Signature,
            requestType: null,
            typeof(TResponse));
    }

    private static Task<TResponse> Describe(CancellationToken cancellationToken) => throw new UnreachableException();
}

/// <summary>FastEndpoints-style request endpoint that writes its response through <see cref="Send"/>.</summary>
/// <typeparam name="TRequest">The request model, bound from the JSON body unless <see cref="BindAsync"/> is overridden.</typeparam>
public abstract class Endpoint<TRequest> : EndpointBase
{
    private static readonly MethodInfo Signature = ((Func<TRequest, CancellationToken, Task<IResult>>)Describe).Method;

    private JsonTypeInfo<TRequest>? requestJson;

    /// <summary>Handles one request.</summary>
    public abstract ValueTask HandleAsync(TRequest request, CancellationToken cancellationToken);

    /// <summary>Sets the response for the current request. Without a call the endpoint answers 204.</summary>
    protected EndpointSender Send => new(this);

    /// <summary>Binds the request model. Reads the JSON body by default; throw <see cref="BadHttpRequestException"/> to reject.</summary>
    protected virtual ValueTask<TRequest> BindAsync(CancellationToken cancellationToken) =>
        EndpointJson.ReadBodyAsync(this.HttpContext, this.requestJson!, cancellationToken);

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var requestJson = EndpointJson.GetRequestTypeInfo<TRequest>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<Endpoint<TRequest>>(context, endpointType);
                endpoint.requestJson = requestJson;
                var cancellationToken = context.RequestAborted;
                ValueTask<TRequest> binding;
                try
                {
                    binding = endpoint.BindAsync(cancellationToken);
                }
                catch (BadHttpRequestException exception)
                {
                    return Reject(context, exception);
                }

                return binding.IsCompletedSuccessfully
                    ? EndpointSender.RespondAsync(context, endpoint.HandleAsync(binding.Result, cancellationToken), endpoint)
                    : CompleteAsync(context, endpoint, binding, cancellationToken);
            },
            Signature,
            typeof(TRequest),
            typeof(IResult));
    }

    private static async Task CompleteAsync(HttpContext context, Endpoint<TRequest> endpoint, ValueTask<TRequest> binding, CancellationToken cancellationToken)
    {
        TRequest request;
        try
        {
            request = await binding.ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            await Reject(context, exception).ConfigureAwait(false);
            return;
        }

        await EndpointSender.RespondAsync(context, endpoint.HandleAsync(request, cancellationToken), endpoint).ConfigureAwait(false);
    }

    private static Task<IResult> Describe(TRequest request, CancellationToken cancellationToken) => throw new UnreachableException();
}

/// <summary>FastEndpoints-style endpoint without a request body that writes through <see cref="Send"/>.</summary>
public abstract class EndpointWithoutRequest : EndpointBase
{
    private static readonly MethodInfo Signature = ((Func<CancellationToken, Task<IResult>>)Describe).Method;

    /// <summary>Handles one request.</summary>
    public abstract ValueTask HandleAsync(CancellationToken cancellationToken);

    /// <summary>Sets the response for the current request. Without a call the endpoint answers 204.</summary>
    protected EndpointSender Send => new(this);

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType) =>
        definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<EndpointWithoutRequest>(context, endpointType);
                return EndpointSender.RespondAsync(context, endpoint.HandleAsync(context.RequestAborted), endpoint);
            },
            Signature,
            requestType: null,
            typeof(IResult));

    private static Task<IResult> Describe(CancellationToken cancellationToken) => throw new UnreachableException();
}

/// <summary>Sets the response of a request-only endpoint. A thin view over the endpoint, so it allocates nothing.</summary>
public readonly struct EndpointSender
{
    private readonly EndpointBase endpoint;

    internal EndpointSender(EndpointBase endpoint) => this.endpoint = endpoint;

    public ValueTask OkAsync<T>(T response, CancellationToken cancellationToken = default) => this.Set(TypedResults.Ok(response));
    public ValueTask CreatedAsync<T>(string uri, T response, CancellationToken cancellationToken = default) => this.Set(TypedResults.Created(uri, response));
    public ValueTask AcceptedAsync<T>(string? uri, T response, CancellationToken cancellationToken = default) => this.Set(TypedResults.Accepted(uri, response));
    public ValueTask NotFoundAsync(CancellationToken cancellationToken = default) => this.Set(TypedResults.NotFound());
    public ValueTask NoContentAsync(CancellationToken cancellationToken = default) => this.Set(TypedResults.NoContent());
    public ValueTask ResponseAsync(IResult response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return this.Set(response);
    }

    private ValueTask Set(IResult response)
    {
        (this.endpoint ?? throw new InvalidOperationException("Send is only available inside an endpoint.")).SentResult = response;
        return ValueTask.CompletedTask;
    }

    // Completed handlers execute the result without an async state machine.
    internal static Task RespondAsync(HttpContext context, ValueTask handled, EndpointBase endpoint)
    {
        if (!handled.IsCompletedSuccessfully)
        {
            return AwaitAsync(context, handled, endpoint);
        }

        handled.GetAwaiter().GetResult();
        return (endpoint.SentResult ?? TypedResults.NoContent()).ExecuteAsync(context);
    }

    private static async Task AwaitAsync(HttpContext context, ValueTask handled, EndpointBase endpoint)
    {
        await handled.ConfigureAwait(false);
        await (endpoint.SentResult ?? TypedResults.NoContent()).ExecuteAsync(context).ConfigureAwait(false);
    }
}
