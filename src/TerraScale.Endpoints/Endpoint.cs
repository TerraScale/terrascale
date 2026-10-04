using System.ComponentModel.DataAnnotations;
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

    private RequestBinder<TRequest>? binder;

    /// <summary>Handles one request.</summary>
    /// <remarks>Return <c>ValueTask.FromResult(value)</c> (or write an <c>async</c> method) to avoid a task allocation per request.</remarks>
    public abstract ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);

    /// <summary>Binds the request model. Reads the JSON body by default; throw <see cref="BadHttpRequestException"/> to reject.</summary>
    protected virtual ValueTask<TRequest> BindAsync(CancellationToken cancellationToken) =>
        this.binder!.BindAsync(this.HttpContext, cancellationToken);

    /// <summary>
    /// Endpoint-specific validation that can use the endpoint's injected services, for example a
    /// database uniqueness check. Runs after binding together with data annotation validation
    /// (<c>AddValidation()</c>, including .NET 11 <see cref="AsyncValidationAttribute"/> and
    /// <see cref="IAsyncValidatableObject"/>); all errors are answered as one 400 validation problem
    /// before <c>HandleAsync</c> runs. Yield a <see cref="ValidationResult"/> per error, naming its members.
    /// </summary>
    protected virtual IAsyncEnumerable<ValidationResult> ValidateAsync(TRequest request, CancellationToken cancellationToken) =>
        NoValidation;

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var binder = new RequestBinder<TRequest>(routes.ServiceProvider);
        var responseJson = EndpointJson.GetResponseTypeInfo<TResponse>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<Endpoint<TRequest, TResponse>>(context, endpointType);
                endpoint.binder = binder;
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

                if (!binding.IsCompletedSuccessfully || binder.Validates)
                {
                    return CompleteAsync(context, endpoint, binder, binding, rules: null, responseJson, cancellationToken);
                }

                // Endpoints without a ValidateAsync override return the shared sentinel and stay synchronous.
                // The binding was consumed through Result, so the continuation gets the bound value
                // instead: a ValueTask backed by an IValueTaskSource may be awaited only once.
                var request = binding.Result;
                var rules = endpoint.ValidateAsync(request, cancellationToken);
                return ReferenceEquals(rules, NoValidation)
                    ? EndpointJson.RespondAsync(context, endpoint.HandleAsync(request, cancellationToken), responseJson)
                    : CompleteAsync(context, endpoint, binder, new ValueTask<TRequest>(request), rules, responseJson, cancellationToken);
            },
            Signature,
            typeof(TRequest),
            typeof(TResponse),
            binder.Properties);
    }

    private static async Task CompleteAsync(
        HttpContext context,
        Endpoint<TRequest, TResponse> endpoint,
        RequestBinder<TRequest> binder,
        ValueTask<TRequest> binding,
        IAsyncEnumerable<ValidationResult>? rules,
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

        rules ??= endpoint.ValidateAsync(request, cancellationToken);
        var endpointRules = ReferenceEquals(rules, NoValidation) ? null : rules;
        if (!await binder.ValidateAsync(context, request, endpointRules, cancellationToken).ConfigureAwait(false))
        {
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

    private RequestBinder<TRequest>? binder;

    /// <summary>Handles one request.</summary>
    public abstract ValueTask HandleAsync(TRequest request, CancellationToken cancellationToken);

    /// <summary>Sets the response for the current request. Without a call the endpoint answers 204.</summary>
    protected EndpointSender Send => new(this);

    /// <summary>Binds the request model. Reads the JSON body by default; throw <see cref="BadHttpRequestException"/> to reject.</summary>
    protected virtual ValueTask<TRequest> BindAsync(CancellationToken cancellationToken) =>
        this.binder!.BindAsync(this.HttpContext, cancellationToken);

    /// <summary>
    /// Endpoint-specific validation that can use the endpoint's injected services, for example a
    /// database uniqueness check. Runs after binding together with data annotation validation
    /// (<c>AddValidation()</c>, including .NET 11 <see cref="AsyncValidationAttribute"/> and
    /// <see cref="IAsyncValidatableObject"/>); all errors are answered as one 400 validation problem
    /// before <c>HandleAsync</c> runs. Yield a <see cref="ValidationResult"/> per error, naming its members.
    /// </summary>
    protected virtual IAsyncEnumerable<ValidationResult> ValidateAsync(TRequest request, CancellationToken cancellationToken) =>
        NoValidation;

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var binder = new RequestBinder<TRequest>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<Endpoint<TRequest>>(context, endpointType);
                endpoint.binder = binder;
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

                if (!binding.IsCompletedSuccessfully || binder.Validates)
                {
                    return CompleteAsync(context, endpoint, binder, binding, rules: null, cancellationToken);
                }

                // Endpoints without a ValidateAsync override return the shared sentinel and stay synchronous.
                // The binding was consumed through Result, so the continuation gets the bound value
                // instead: a ValueTask backed by an IValueTaskSource may be awaited only once.
                var request = binding.Result;
                var rules = endpoint.ValidateAsync(request, cancellationToken);
                return ReferenceEquals(rules, NoValidation)
                    ? EndpointSender.RespondAsync(context, endpoint.HandleAsync(request, cancellationToken), endpoint)
                    : CompleteAsync(context, endpoint, binder, new ValueTask<TRequest>(request), rules, cancellationToken);
            },
            Signature,
            typeof(TRequest),
            typeof(IResult),
            binder.Properties);
    }

    private static async Task CompleteAsync(
        HttpContext context,
        Endpoint<TRequest> endpoint,
        RequestBinder<TRequest> binder,
        ValueTask<TRequest> binding,
        IAsyncEnumerable<ValidationResult>? rules,
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

        rules ??= endpoint.ValidateAsync(request, cancellationToken);
        var endpointRules = ReferenceEquals(rules, NoValidation) ? null : rules;
        if (!await binder.ValidateAsync(context, request, endpointRules, cancellationToken).ConfigureAwait(false))
        {
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

    /// <summary>Answers 200 with a JSON body.</summary>
    public ValueTask OkAsync<T>(T response, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Ok(response));

    /// <summary>Answers 201 with a <c>Location</c> header and a JSON body.</summary>
    public ValueTask CreatedAsync<T>(string uri, T response, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Created(uri, response));

    /// <summary>Answers 202 with an optional <c>Location</c> header and a JSON body.</summary>
    public ValueTask AcceptedAsync<T>(string? uri, T response, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Accepted(uri, response));

    /// <summary>Answers 404 without a body.</summary>
    public ValueTask NotFoundAsync(CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.NotFound());

    /// <summary>Answers 204.</summary>
    public ValueTask NoContentAsync(CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.NoContent());

    /// <summary>Answers 400 without a body.</summary>
    public ValueTask BadRequestAsync(CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.BadRequest());

    /// <summary>Answers 400 with a JSON body.</summary>
    public ValueTask BadRequestAsync<T>(T error, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.BadRequest(error));

    /// <summary>Answers 401 without a body.</summary>
    public ValueTask UnauthorizedAsync(CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Unauthorized());

    /// <summary>Answers 403 without a body or an authentication challenge.</summary>
    public ValueTask ForbiddenAsync(CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.StatusCode(StatusCodes.Status403Forbidden));

    /// <summary>Answers 409 without a body.</summary>
    public ValueTask ConflictAsync(CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Conflict());

    /// <summary>Answers 409 with a JSON body.</summary>
    public ValueTask ConflictAsync<T>(T error, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Conflict(error));

    /// <summary>Answers with a bare status code.</summary>
    public ValueTask StatusCodeAsync(int statusCode, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.StatusCode(statusCode));

    /// <summary>Answers 302, or 301 when <paramref name="permanent"/> is set.</summary>
    public ValueTask RedirectAsync(string url, bool permanent = false, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Redirect(url, permanent));

    /// <summary>Answers with an <c>application/problem+json</c> body.</summary>
    public ValueTask ProblemAsync(
        string? detail = null,
        int statusCode = StatusCodes.Status500InternalServerError,
        string? title = null,
        CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.Problem(detail, statusCode: statusCode, title: title));

    /// <summary>Answers 400 with a validation problem listing errors per field.</summary>
    public ValueTask ValidationProblemAsync(IDictionary<string, string[]> errors, CancellationToken cancellationToken = default) =>
        this.Set(TypedResults.ValidationProblem(errors));

    /// <summary>Answers with any <see cref="IResult"/>.</summary>
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
