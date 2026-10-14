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
        builder.Services.AddSingleton<IRebuildCertification, RebuildCertification>();
        builder.Services.AddSingleton<IReplicaRebuild, ReplicaRebuild>();
        builder.Services.AddSingleton<PeerStreamReceiver>();
        builder.Services.AddSingleton<CatalogGate>();
        builder.Services.AddSingleton<ICatalogGate>(provider => provider.GetRequiredService<CatalogGate>());
        // Verify before catalog recovery, platform seeding and any background catalog work.
        builder.Services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(provider => provider.GetRequiredService<CatalogGate>()));
        // Recover SYSTEM before catalog inventory, platform seeding and backup snapshot work starts.
        builder.Services.Insert(1, ServiceDescriptor.Singleton<IHostedService, RebuildRecovery>());
        builder.Services.AddSingleton<AntiEntropy>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<AntiEntropy>());
        builder.Services.AddSingleton<RemoteAckWaiter>();
        builder.Services.AddSingleton<PeerStreams>();
        builder.Services.AddSingleton<IReplicationStatus>(provider => provider.GetRequiredService<PeerStreams>());
        // Decision 0013: a machine of a cell (it has a member directory, as the membership does) confirms writes remotely.
        builder.Services.AddSingleton<IRemoteDurability>(provider =>
            provider.GetService<Engine.Cluster.IMemberDirectory>() is null ? new LocalDurability() : provider.GetRequiredService<PeerStreams>());
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
