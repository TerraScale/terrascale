using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.OpenApi;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Features.Pressure;

namespace TerraScale.TerraBase.Hosting;

/// <summary>The listeners TerraBase serves; every endpoint belongs to exactly one.</summary>
public enum TerraBaseListener
{
    /// <summary>Loopback-only administration: health, metrics, OpenAPI and operator endpoints.</summary>
    Admin,

    /// <summary>Client-facing API over TLS, authorized by scoped API keys.</summary>
    Client,

    /// <summary>Inter-node gRPC over mutual TLS.</summary>
    Peer,
}

/// <summary>
/// Endpoint metadata naming the listeners that serve an endpoint. Endpoints without it belong to
/// <see cref="TerraBaseListener.Admin"/>. Add it with <c>Metadata(ListenerMetadata.Client)</c>.
/// Requests on a connection that no TerraBase listener accepted are never served.
/// </summary>
/// <param name="Listeners">The serving listeners.</param>
public sealed record ListenerMetadata(params IReadOnlyList<TerraBaseListener> Listeners)
{
    /// <summary>The listener of endpoints without metadata.</summary>
    public static ListenerMetadata Admin { get; } = new(TerraBaseListener.Admin);

    /// <summary>Metadata for client-facing endpoints.</summary>
    public static ListenerMetadata Client { get; } = new(TerraBaseListener.Client);

    /// <summary>Metadata for inter-node endpoints.</summary>
    public static ListenerMetadata Peer { get; } = new(TerraBaseListener.Peer);

    /// <summary>
    /// Metadata for unauthenticated health probes, served on the administrative listener and, for load balancers, on the
    /// client listener. They never answer with tenant data.
    /// </summary>
    public static ListenerMetadata Probe { get; } = new(TerraBaseListener.Admin, TerraBaseListener.Client);
}

/// <summary>Binds listeners and keeps each endpoint on its own listener.</summary>
public static class Listeners
{
    private static readonly object ListenerKey = typeof(TerraBaseListener);

    /// <summary>The actual listener that accepted the connection, or null for an untagged connection.</summary>
    public static TerraBaseListener? ArrivedOn(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ListenerKey, out var tag) == true
            ? tag as TerraBaseListener?
            : null;
    }

    /// <summary>Documents the client listener's region headers and pin rejection in OpenAPI.</summary>
    public static IServiceCollection AddTerraBaseRegionOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenApi("v1", options => options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ListenerMetadata>()
                .Any(metadata => metadata.Listeners.Contains(TerraBaseListener.Client)))
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-TerraScale-Region-Pin",
                    In = ParameterLocation.Header,
                    Description = "Optional single raw region ID. An empty, duplicate or mismatched pin returns 421 before execution; re-resolve through Atlas.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                });
                operation.Responses ??= [];
                operation.Responses["421"] = new OpenApiResponse
                {
                    Description = "Misdirected Request; X-TerraBase-Region identifies the serving node.",
                    Content = new Dictionary<string, IOpenApiMediaType>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new OpenApiMediaType { Schema = await context.GetOrCreateSchemaAsync(typeof(ErrorBody), cancellationToken: cancellationToken) },
                    },
                };
                foreach (var response in operation.Responses.Values.OfType<OpenApiResponse>())
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                    response.Headers["X-TerraBase-Region"] = new OpenApiHeader
                    {
                        Description = "Region of the serving TerraBase node (client listener only).",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    };
                }
            }
        }));
        return services;
    }

    /// <summary>Tags every connection accepted by <paramref name="listen"/> with its listener.</summary>
    public static ListenOptions UseTerraBaseListener(this ListenOptions listen, TerraBaseListener listener)
    {
        ArgumentNullException.ThrowIfNull(listen);
        listen.Use(next => async connection =>
        {
            connection.Items[ListenerKey] = listener;
            var pressure = listener == TerraBaseListener.Client ? listen.ApplicationServices.GetRequiredService<IPressureSignals>() : null;
            pressure?.ConnectionOpened();
            try
            {
                await next(connection).ConfigureAwait(false);
            }
            finally
            {
                pressure?.ConnectionClosed();
            }
        });
        return listen;
    }

    /// <summary>Adds the client-facing HTTPS listener on every interface when <c>client-port</c> is set.</summary>
    public static void AddTerraBaseClientListener(this KestrelServerOptions kestrel, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClientPort is not { } port)
        {
            return;
        }

        kestrel.ListenAnyIP(port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseTerraBaseListener(TerraBaseListener.Client);
            listen.UseHttps(options.ClientCertificate!);
        });
    }

    /// <summary>
    /// Answers 404 when a request reaches an endpoint through a listener that does not serve it, or
    /// arrives on a connection without a TerraBase listener tag.
    /// </summary>
    public static WebApplication UseTerraBaseListeners(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var region = app.Services.GetRequiredService<TerraBaseOptions>().Region;
        app.UseRouting();
        app.Use((context, next) =>
        {
            var arrivedOn = ArrivedOn(context);
            if (arrivedOn == TerraBaseListener.Client)
            {
                // OnStarting survives Response.Clear() in the error middleware.
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-TerraBase-Region"] = region;
                    return Task.CompletedTask;
                });
                if (context.Request.Headers.TryGetValue("X-TerraScale-Region-Pin", out var pin)
                    && (pin.Count != 1 || !string.Equals(pin[0], region, StringComparison.Ordinal)))
                {
                    context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    return Task.CompletedTask;
                }
            }

            var servedBy = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>()?.Listeners ?? ListenerMetadata.Admin.Listeners;
            if (arrivedOn is not { } listener || !servedBy.Contains(listener))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
        return app;
    }
}
