using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Endpoints.Binding;

namespace TerraScale.Endpoints;

public abstract class Endpoint<TRequest, TResponse> : EndpointBase
{
    private static readonly MethodInfo Signature = ((Func<TRequest, CancellationToken, Task<TResponse>>)Describe).Method;

    private RequestBinder<TRequest>? binder;

    public abstract ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);

    protected virtual ValueTask<Bound<TRequest>> BindAsync(CancellationToken cancellationToken) =>
        (binder ?? throw new InvalidOperationException("Endpoint binder is only available while handling a request."))
            .BindAsync(HttpContext, cancellationToken);

    protected virtual IAsyncEnumerable<ValidationResult> ValidateAsync(TRequest request, CancellationToken cancellationToken) =>
        NoValidation;

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var binder = new RequestBinder<TRequest>(routes.ServiceProvider);
        var (responseJson, unionWriter, unionProduces) = EndpointJson.ResolveResponse<TResponse>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<Endpoint<TRequest, TResponse>>(context, endpointType);
                endpoint.binder = binder;
                var cancellationToken = context.RequestAborted;
                var binding = endpoint.BindAsync(cancellationToken);

                if (!binding.IsCompletedSuccessfully || binder.Validates)
                {
                    return CompleteAsync(context, endpoint, binder, binding, rules: null, responseJson, unionWriter, cancellationToken);
                }

                var bound = binding.Result;
                return bound switch
                {
                    Validation invalid => Reject(context, invalid),
                    UnsupportedMediaType unsupported => Reject(context, unsupported),
                    null => throw new UnreachableException(),
                    TRequest request => Respond(context, endpoint, binder, request, responseJson, unionWriter, cancellationToken),
                };
            },
            Signature,
            typeof(TRequest),
            typeof(TResponse),
            UsesDefaultBinding(typeof(Endpoint<TRequest, TResponse>)) ? binder.Properties : null,
            binder.Validates,
            unionProduces);
    }

    private static Task Respond(
        HttpContext context,
        Endpoint<TRequest, TResponse> endpoint,
        RequestBinder<TRequest> binder,
        TRequest request,
        JsonTypeInfo<TResponse>? responseJson,
        IUnionResponseWriter<TResponse>? unionWriter,
        CancellationToken cancellationToken)
    {
        var rules = endpoint.ValidateAsync(request, cancellationToken);
        return ReferenceEquals(rules, NoValidation)
            ? EndpointJson.RespondAsync(context, endpoint.HandleAsync(request, cancellationToken), responseJson, unionWriter)
            : CompleteAsync(context, endpoint, binder, new ValueTask<Bound<TRequest>>(request), rules, responseJson, unionWriter, cancellationToken);
    }

    private static async Task CompleteAsync(
        HttpContext context,
        Endpoint<TRequest, TResponse> endpoint,
        RequestBinder<TRequest> binder,
        ValueTask<Bound<TRequest>> binding,
        IAsyncEnumerable<ValidationResult>? rules,
        JsonTypeInfo<TResponse>? responseJson,
        IUnionResponseWriter<TResponse>? unionWriter,
        CancellationToken cancellationToken)
    {
        var bound = await binding.ConfigureAwait(false);
        await (bound switch
        {
            Validation invalid => Reject(context, invalid),
            UnsupportedMediaType unsupported => Reject(context, unsupported),
            null => throw new UnreachableException(),
            TRequest parsed => ValidateAndHandleAsync(context, endpoint, binder, parsed, rules, responseJson, unionWriter, cancellationToken),
        }).ConfigureAwait(false);
    }

    private static async Task ValidateAndHandleAsync(
        HttpContext context,
        Endpoint<TRequest, TResponse> endpoint,
        RequestBinder<TRequest> binder,
        TRequest request,
        IAsyncEnumerable<ValidationResult>? rules,
        JsonTypeInfo<TResponse>? responseJson,
        IUnionResponseWriter<TResponse>? unionWriter,
        CancellationToken cancellationToken)
    {
        rules ??= endpoint.ValidateAsync(request, cancellationToken);
        var endpointRules = ReferenceEquals(rules, NoValidation) ? null : rules;
        if (!await binder.ValidateAsync(context, request, endpointRules, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await EndpointJson.RespondAsync(context, endpoint.HandleAsync(request, cancellationToken), responseJson, unionWriter).ConfigureAwait(false);
    }

    private static Task<TResponse> Describe(TRequest request, CancellationToken cancellationToken) => throw new UnreachableException();
}

public abstract class EndpointWithoutRequest<TResponse> : EndpointBase
{
    private static readonly MethodInfo Signature = ((Func<CancellationToken, Task<TResponse>>)Describe).Method;

    public abstract ValueTask<TResponse> HandleAsync(CancellationToken cancellationToken);

    private protected sealed override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes, EndpointDefinition definition, Type endpointType)
    {
        var (responseJson, unionWriter, unionProduces) = EndpointJson.ResolveResponse<TResponse>(routes.ServiceProvider);
        return definition.Map(
            routes,
            context =>
            {
                var endpoint = Activate<EndpointWithoutRequest<TResponse>>(context, endpointType);
                return EndpointJson.RespondAsync(context, endpoint.HandleAsync(context.RequestAborted), responseJson, unionWriter);
            },
            Signature,
            requestType: null,
            typeof(TResponse),
            unionProduces: unionProduces);
    }

    private static Task<TResponse> Describe(CancellationToken cancellationToken) => throw new UnreachableException();
}
