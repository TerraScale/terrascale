using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.OpenApi;
using TerraBase.Configuration;
using TerraBase.Features.Management;
using TerraBase.Features.Pressure;

namespace TerraBase.Hosting;

/// <summary>The listeners TerraBase serves.</summary>
public enum TerraBaseListener
{
    /// <summary>
    /// Fly's private network only (<c>fly-local-6pn</c>), HTTP/2 without TLS: inter-node gRPC, administration and the
    /// platform operator's API. The network is the trust boundary; no service credential is presented.
    /// </summary>
    Internal,

    /// <summary>Public API, plain HTTP behind Fly Proxy (TLS ends at the edge), authorized by scoped API keys and ts-auth tokens.</summary>
    Client,

    /// <summary>The Prometheus scrape Fly's metrics collector reads (<c>[[metrics]]</c>); every interface, no Fly Proxy service.</summary>
    Metrics,
}

/// <summary>
/// Endpoint metadata naming the listeners that serve an endpoint. Endpoints without it belong to
/// <see cref="TerraBaseListener.Internal"/>. Add it with <c>Metadata(ListenerMetadata.Client)</c>.
/// Requests on a connection that no TerraBase listener accepted are never served.
/// </summary>
/// <param name="Listeners">The serving listeners.</param>
public sealed record ListenerMetadata(params IReadOnlyList<TerraBaseListener> Listeners)
{
    /// <summary>The listener of endpoints without metadata: inter-node services and administration.</summary>
    public static ListenerMetadata Internal { get; } = new(TerraBaseListener.Internal);

    /// <summary>Metadata for the client API: the public listener, and the internal listener for the platform operator.</summary>
    public static ListenerMetadata Client { get; } = new(TerraBaseListener.Client, TerraBaseListener.Internal);

    /// <summary>
    /// Metadata for unauthenticated health probes, served on the internal listener and, for Fly's health checks, on the
    /// public listener. They never answer with tenant data.
    /// </summary>
    public static ListenerMetadata Probe { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Client);

    /// <summary>
    /// Metadata for the Management API: ts-auth access tokens on the public listener, and the platform operator on the
    /// internal listener.
    /// </summary>
    public static ListenerMetadata Management { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Client);

    /// <summary>Metadata for the Prometheus scrape: the internal listener and the metrics listener.</summary>
    public static ListenerMetadata Metrics { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Metrics);
}

/// <summary>Request feature: the request is answered as a node of <paramref name="Region"/> (see <see cref="Listeners.RegionOf"/>).</summary>
/// <param name="Region">The answering node's region.</param>
public sealed record AnsweringRegion(string Region);

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

    /// <summary>Connection items for a request served in process as if <paramref name="listener"/> accepted it.</summary>
    internal static IConnectionItemsFeature AcceptedBy(TerraBaseListener listener) => new AcceptedConnection(listener);

    /// <summary>
    /// The region of the node that answers the request (docs/region-identity.md): this node's, or for a request a catalog
    /// follower forwarded, the follower's (<see cref="AnsweringRegion"/>).
    /// </summary>
    public static string RegionOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<AnsweringRegion>()?.Region ?? context.RequestServices.GetRequiredService<TerraBaseOptions>().Region;
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

    /// <summary>
    /// The address of the internal listener: the machine's private address on Fly, which Fly lists as <c>fly-local-6pn</c>
    /// in <c>/etc/hosts</c>; loopback off Fly.
    /// </summary>
    public static IPAddress InternalAddress(TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return options.App is null ? IPAddress.Loopback
                : Dns.GetHostAddresses("fly-local-6pn").First(address => address.AddressFamily == AddressFamily.InterNetworkV6);
        }
        catch (Exception error) when (error is SocketException or InvalidOperationException)
        {
            throw new TerraBaseConfigurationException("fly-local-6pn must resolve to the machine's private IPv6 address");
        }
    }

    /// <summary>
    /// Binds the internal listener on <paramref name="internalAddress"/>, then the public and the metrics listeners on every
    /// interface, in that order.
    /// </summary>
    public static void AddTerraBaseListeners(this KestrelServerOptions kestrel, TerraBaseOptions options, IPAddress internalAddress)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(options);
        // HTTP/2 only: without TLS there is no ALPN, and gRPC needs HTTP/2 prior knowledge.
        kestrel.Listen(internalAddress, options.InternalPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseTerraBaseListener(TerraBaseListener.Internal);
        });
        kestrel.ListenAnyIP(options.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseTerraBaseListener(TerraBaseListener.Client);
        });
        kestrel.ListenAnyIP(options.MetricsPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseTerraBaseListener(TerraBaseListener.Metrics);
        });
    }

    /// <summary>
    /// Answers 404 when a request reaches an endpoint through a listener that does not serve it, or
    /// arrives on a connection without a TerraBase listener tag.
    /// </summary>
    public static WebApplication UseTerraBaseListeners(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) =>
        {
            var arrivedOn = ArrivedOn(context);
            if (arrivedOn == TerraBaseListener.Client)
            {
                var region = RegionOf(context);
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

            var servedBy = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>()?.Listeners ?? ListenerMetadata.Internal.Listeners;
            if (arrivedOn == TerraBaseListener.Internal
                && context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                && context.Features.Get<LegacyManagementRoute>() is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            if (arrivedOn is not { } listener || !servedBy.Contains(listener))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
        return app;
    }

    private sealed class AcceptedConnection(TerraBaseListener listener) : IConnectionItemsFeature
    {
        public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?> { [ListenerKey] = listener };
    }
}
