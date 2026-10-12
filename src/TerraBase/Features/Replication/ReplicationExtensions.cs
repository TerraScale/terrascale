using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>Registers the catalog's inter-node gRPC services on the internal listener.</summary>
public static class ReplicationExtensions
{
    /// <summary>
    /// Registers the <c>CatalogReplication</c> services, and the catalog follower and <see cref="ICatalogPrimaryClient"/>,
    /// which only a machine that follows a catalog primary runs. Payloads are LZ4 application envelopes; gRPC transport
    /// compression is not used.
    /// </summary>
    public static WebApplicationBuilder AddTerraBaseReplication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // The role comes from the machine's identity, which the registered options carry.
        builder.Services.AddSingleton<ICatalogReadiness>(provider => provider.GetRequiredService<TerraBaseOptions>().CatalogFollower
            ? provider.GetRequiredService<CatalogFollower>()
            : new CatalogPrimaryReadiness());
        // Nothing replicates client databases until the CDC peer streams arrive (docs/terrabase/multi-region-replication.md).
        builder.Services.AddSingleton<IReplicationStatus, NoReplicationStatus>();
        builder.Services.AddSingleton<IReplicationPressure, NoReplicationPressure>();
        builder.Services.AddSingleton<CatalogFollower>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CatalogFollower>());
        builder.Services.AddSingleton<CatalogForwarder>();
        builder.Services.AddSingleton<ICatalogPrimaryClient, CatalogPrimaryClient>();
        builder.Services.AddSingleton<CatalogFeed>();
        builder.Services.AddSingleton<ForwardedRequests>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes;
            grpc.MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes;
        });
        return builder;
    }

    /// <summary>Maps the catalog's inter-node gRPC services onto the internal listener.</summary>
    public static WebApplication MapTerraBaseReplication(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGrpcService<CatalogFeed>().WithMetadata(ListenerMetadata.Internal);
        app.MapGrpcService<ForwardedRequests>().WithMetadata(ListenerMetadata.Internal);
        return app;
    }

    /// <summary>
    /// Takes the application pipeline that serves requests a catalog follower forwards; register it first, so a forwarded
    /// request passes every middleware a request accepted by a listener does.
    /// </summary>
    public static WebApplication UseTerraBaseForwardedRequests(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<ForwardedRequests>() is { } forwarded)
        {
            app.Use(forwarded.Capture);
        }

        return app;
    }

    /// <summary>
    /// Register after authorization: a catalog follower forwards an authenticated client-listener request to an endpoint the
    /// catalog primary serves (<see cref="ServedByCatalogPrimary"/>) to its primary, and the primary serves a forwarded request
    /// only on such an endpoint (404 otherwise). The administrative listener's requests are never forwarded.
    /// </summary>
    public static WebApplication UseTerraBaseCatalogForwarding(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var forwarder = app.Services.GetRequiredService<TerraBaseOptions>().CatalogFollower ? app.Services.GetService<CatalogForwarder>() : null;
        app.Use((context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (context.Features.Get<ForwardedRequests.ForwardedRequest>() is not null)
            {
                if (ServedByCatalogPrimary.Serves(endpoint))
                {
                    return next(context);
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            if (forwarder is null || Listeners.ArrivedOn(context) != TerraBaseListener.Client)
            {
                return next(context);
            }

            return ServedByCatalogPrimary.Always(endpoint) ? forwarder.ForwardAsync(context)
                : ServedByCatalogPrimary.Serves(endpoint) ? forwarder.ForwardWhenWritingAsync(context, next)
                : next(context);
        });
        return app;
    }
}
