using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TerraScale.TerraBase.Configuration;

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
/// Endpoint metadata naming the listener that serves an endpoint. Endpoints without it belong to
/// <see cref="TerraBaseListener.Admin"/>. Add it with <c>Metadata(ListenerMetadata.Client)</c>.
/// </summary>
/// <param name="Listener">The serving listener.</param>
public sealed record ListenerMetadata(TerraBaseListener Listener)
{
    /// <summary>Metadata for client-facing endpoints.</summary>
    public static ListenerMetadata Client { get; } = new(TerraBaseListener.Client);

    /// <summary>Metadata for inter-node endpoints.</summary>
    public static ListenerMetadata Peer { get; } = new(TerraBaseListener.Peer);
}

/// <summary>Binds listeners and keeps each endpoint on its own listener.</summary>
public static class Listeners
{
    private static readonly object ListenerKey = typeof(TerraBaseListener);

    /// <summary>Tags every connection accepted by <paramref name="listen"/> with its listener.</summary>
    public static ListenOptions UseTerraBaseListener(this ListenOptions listen, TerraBaseListener listener)
    {
        ArgumentNullException.ThrowIfNull(listen);
        listen.Use(next => connection =>
        {
            connection.Items[ListenerKey] = listener;
            return next(connection);
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
            listen.UseHttps(options.ClientCertificate!);
            listen.UseTerraBaseListener(TerraBaseListener.Client);
        });
    }

    /// <summary>Answers 404 when a request reaches an endpoint through a listener that does not serve it.</summary>
    public static WebApplication UseTerraBaseListeners(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseRouting();
        app.Use((context, next) =>
        {
            var arrivedOn = context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ListenerKey, out var tag) == true
                ? (TerraBaseListener)tag!
                : TerraBaseListener.Admin;
            var servedBy = context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>()?.Listener ?? TerraBaseListener.Admin;
            if (arrivedOn != servedBy)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
        return app;
    }
}
