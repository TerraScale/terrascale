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

    internal byte ListenerMask { get; } = ComputeMask(Listeners);

    internal bool Serves(TerraBaseListener listener) => (ListenerMask & (1 << (int)listener)) != 0;

    private static byte ComputeMask(IReadOnlyList<TerraBaseListener> listeners)
    {
        byte mask = 0;
        for (var index = 0; index < listeners.Count; index++)
        {
            mask |= (byte)(1 << (int)listeners[index]);
        }

        return mask;
    }
}

internal sealed class ListenerConnections(int limit)
{
    private int _open;

    public bool TryOpen()
    {
        if (Interlocked.Increment(ref _open) <= limit)
        {
            return true;
        }

        _ = Interlocked.Decrement(ref _open);
        return false;
    }

    public void Close() => _ = Interlocked.Decrement(ref _open);
}

internal static class Listeners
{
    public const int MaxPublicConnections = 16 * 1024;

    public const int MaxUpgradedConnections = 8 * 1024;

    private static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(10);

    private static readonly object ListenerKey = typeof(TerraBaseListener);

    private static readonly ListenerTag[] Tags =
    [
        new(TerraBaseListener.Internal),
        new(TerraBaseListener.Client),
        new(TerraBaseListener.Metrics),
        new(TerraBaseListener.Postgres),
    ];

    public static TerraBaseListener? ArrivedOn(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ListenerKey, out var value) == true)
        {
            if (value is ListenerTag tag)
            {
                return tag.Listener;
            }

            if (value is TerraBaseListener listener)
            {
                return listener;
            }
        }

        return null;
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
            kestrel.Limits.MaxConcurrentUpgradedConnections = MaxUpgradedConnections;
            kestrel.Limits.RequestHeadersTimeout = RequestHeadersTimeout;
            var loopbackOnly = options.PeersFile is null;
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
            var budget = listener is TerraBaseListener.Client or TerraBaseListener.Postgres ? new ListenerConnections(MaxPublicConnections) : null;
            var tag = Tags[(int)listener];
            listen.Use(next => async connection =>
            {
                if (budget is not null && !budget.TryOpen())
                {
                    connection.Abort();
                    return;
                }

                connection.Items[ListenerKey] = tag;
                var pressure = budget is null ? null : listen.ApplicationServices.GetRequiredService<PressureSignals>();
                pressure?.ConnectionOpened();
                try
                {
                    await next(connection);
                }
                finally
                {
                    pressure?.ConnectionClosed();
                    budget?.Close();
                }
            });
            return listen;
        }
    }

    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseListeners()
        {
            var options = app.Services.GetRequiredService<TerraBaseOptions>();
            var admission = app.Services.GetRequiredService<CallerAdmission>();
            app.Use((context, next) =>
            {
                var arrivedOn = ArrivedOn(context);
                if (arrivedOn is not { } listener)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                var metadata = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>();
                if (listener == TerraBaseListener.Internal && !ReferenceEquals(metadata, ListenerMetadata.Probe))
                {
                    return AdmitInternalAsync(context, next, admission, metadata);
                }

                if (metadata is null || !metadata.Serves(listener))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                if (listener == TerraBaseListener.Client)
                {
                    context.Response.Headers.TryAdd("X-TerraBase-Region", options.Region);
                    context.Response.Headers.TryAdd("X-TerraBase-Machine", options.MachineId);
                }

                return next(context);
            });
            return app;
        }
    }

    private static async Task AdmitInternalAsync(HttpContext context, RequestDelegate next, CallerAdmission admission, ListenerMetadata? metadata)
    {
        if (!await admission.AdmitAsync(context))
        {
            await ErrorResults.WriteAsync(context, StatusCodes.Status401Unauthorized, "unauthorized",
                "the internal listener needs X-TerraBase-Peer-Credential or a valid X-TerraScale-Caller token");
            return;
        }

        if (context.Features.Get<AdmittedCaller>() is { Identity: { } identity }
            && context.Request.Path.Value is { } path
            && !(context.Request.Headers.ContainsKey(CallerAuthentication.UserHeader) && identity.Grants.Contains("act-as")
                && CallerAuthenticationExtensions.ActingPathAllowed(path))
            && !CallerAuthenticationExtensions.ServicePathAllowed(new ServiceCaller(identity.Key.Caller, identity.Grants), path))
        {
            await ErrorResults.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden_caller",
                "the service caller does not grant access to this route");
            return;
        }

        if (metadata is null || !metadata.Serves(TerraBaseListener.Internal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }

    private sealed class ListenerTag(TerraBaseListener listener)
    {
        public TerraBaseListener Listener { get; } = listener;
    }
}
