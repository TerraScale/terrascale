using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Pressure;
using TerraBase.Postgres.Hosting;

namespace TerraBase.Hosting;

public enum TerraBaseListener
{
    Internal,
    Client,
    Metrics,
    Postgres,
}

public sealed record ListenerMetadata(params IReadOnlyList<TerraBaseListener> Listeners)
{
    public static ListenerMetadata Internal { get; } = new(TerraBaseListener.Internal);

    public static ListenerMetadata Client { get; } = new(TerraBaseListener.Client, TerraBaseListener.Internal);

    public static ListenerMetadata Probe { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Client);

    public static ListenerMetadata Management { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Client);

    public static ListenerMetadata Metrics { get; } = new(TerraBaseListener.Internal, TerraBaseListener.Metrics);
}

internal static class Listeners
{
    private static readonly object ListenerKey = typeof(TerraBaseListener);

    public static TerraBaseListener? ArrivedOn(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ListenerKey, out var tag) == true ? tag as TerraBaseListener? : null;
    }

    public static IPAddress InternalAddress(ClusterOptions cluster, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(options);
        if (options.InternalBindAddress is { } explicitAddress)
        {
            return explicitAddress;
        }

        return cluster.ListenEndPoint.Address.Equals(IPAddress.Any) ? IPAddress.Loopback : cluster.ListenEndPoint.Address;
    }

    extension(KestrelServerOptions kestrel)
    {
        public void AddTerraBaseListeners(TerraBaseOptions options, IPAddress internalAddress)
        {
            ArgumentNullException.ThrowIfNull(options);
            var loopbackOnly = options.PeersFile is null && options.IsDevelopment;
            kestrel.Listen(internalAddress, options.InternalPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                listen.UseTerraBaseListener(TerraBaseListener.Internal);
            });
            Bind(options.Port, TerraBaseListener.Client, HttpProtocols.Http1);
            Bind(options.MetricsPort, TerraBaseListener.Metrics, HttpProtocols.Http1);
            Bind(options.PostgresPort, TerraBaseListener.Postgres, HttpProtocols.Http1);

            void Bind(int port, TerraBaseListener listener, HttpProtocols protocols)
            {
                void Configure(ListenOptions listen)
                {
                    listen.Protocols = protocols;
                    listen.UseTerraBaseListener(listener);
                    if (listener == TerraBaseListener.Postgres)
                    {
                        listen.UseTerraBasePostgres();
                    }
                }

                if (listener == TerraBaseListener.Metrics)
                {
                    kestrel.Listen(internalAddress, port, Configure);
                    if (!internalAddress.Equals(IPAddress.Loopback) && !internalAddress.Equals(IPAddress.Any) && !internalAddress.Equals(IPAddress.IPv6Any))
                    {
                        kestrel.Listen(IPAddress.Loopback, port, Configure);
                    }
                }
                else if (loopbackOnly)
                {
                    kestrel.Listen(IPAddress.Loopback, port, Configure);
                }
                else
                {
                    kestrel.ListenAnyIP(port, Configure);
                }
            }
        }
    }

    extension(ListenOptions listen)
    {
        public ListenOptions UseTerraBaseListener(TerraBaseListener listener)
        {
            listen.Use(next => async connection =>
            {
                connection.Items[ListenerKey] = listener;
                var pressure = listener is TerraBaseListener.Client or TerraBaseListener.Postgres
                    ? listen.ApplicationServices.GetRequiredService<PressureSignals>()
                    : null;
                pressure?.ConnectionOpened();
                try
                {
                    await next(connection);
                }
                finally
                {
                    pressure?.ConnectionClosed();
                }
            });
            return listen;
        }
    }

    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseListeners()
        {
            app.Use(async (context, next) =>
            {
                var arrivedOn = ArrivedOn(context);
                var endpoint = context.GetEndpoint();
                var servedBy = endpoint?.Metadata.GetMetadata<ListenerMetadata>()?.Listeners ?? [];
                if (arrivedOn is not { } listener)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (listener == TerraBaseListener.Internal
                    && !ReferenceEquals(endpoint?.Metadata.GetMetadata<ListenerMetadata>(), ListenerMetadata.Probe))
                {
                    if (!await context.RequestServices.GetRequiredService<CallerAdmission>().AdmitAsync(context))
                    {
                        await ErrorResults.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthorized",
                            "the internal listener needs X-TerraBase-Peer-Credential or a valid X-TerraScale-Caller token");
                        return;
                    }

                    if (context.Features.Get<AdmittedCaller>() is { Identity: { } identity }
                        && context.Request.Path.Value is { } path
                        && !CallerAuthenticationExtensions.ServicePathAllowed(new ServiceCaller(identity.Key.Caller, identity.Grants), path))
                    {
                        await ErrorResults.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden_caller",
                            "the service caller does not grant access to this route");
                        return;
                    }
                }

                if (!servedBy.Contains(listener))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var options = context.RequestServices.GetRequiredService<TerraBaseOptions>();
                if (listener == TerraBaseListener.Client)
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers.TryAdd("X-TerraBase-Region", options.Region);
                        context.Response.Headers.TryAdd("X-TerraBase-Machine", options.MachineId);
                        return Task.CompletedTask;
                    });
                }

                await next(context);
            });
            return app;
        }
    }
}
