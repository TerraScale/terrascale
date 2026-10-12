using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>Registers the inter-node gRPC services on the internal listener: the peer streams.</summary>
public static class ReplicationExtensions
{
    /// <summary>
    /// Registers the peer streams (docs/terrabase/multi-region-replication.md sections 8 and 9). Their source, the CDC tailer,
    /// comes with the engine (AddTerraBaseEngine). Payloads are LZ4 application envelopes; gRPC transport compression is
    /// not used.
    /// </summary>
    public static WebApplicationBuilder AddTerraBaseReplication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IChangeGroupSink, StorageChangeGroupSink>();
        builder.Services.AddSingleton<PeerStreamReceiver>();
        builder.Services.AddSingleton<AntiEntropy>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AntiEntropy>());
        builder.Services.AddSingleton<CatalogGate>();
        builder.Services.AddSingleton<ICatalogGate>(provider => provider.GetRequiredService<CatalogGate>());
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CatalogGate>());
        builder.Services.AddSingleton<PeerStreams>();
        builder.Services.AddSingleton<IReplicationStatus>(provider => provider.GetRequiredService<PeerStreams>());
        builder.Services.AddHostedService(provider => provider.GetRequiredService<PeerStreams>());
        builder.Services.AddSingleton<IReplicationPressure, NoReplicationPressure>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = ReplicationProtocol.MaxMessageBytes;
            grpc.MaxSendMessageSize = ReplicationProtocol.MaxMessageBytes;
        });
        return builder;
    }

    /// <summary>Maps the peer streams' receiver onto the internal listener.</summary>
    public static WebApplication MapTerraBaseReplication(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGrpcService<PeerStreamReceiver>().WithMetadata(ListenerMetadata.Internal);
        return app;
    }
}
