using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Storage;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public static class ClusterServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTerraBaseCluster(ClusterOptions options, Func<IServiceProvider, DatabaseSessionResolver> resolver, HlcClock clock, string storageFormatId)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(resolver);
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentException.ThrowIfNullOrEmpty(storageFormatId);
            _ = options.Validate();
            _ = services.AddMetrics();
            services.TryAddSingleton(TimeProvider.System);
            _ = services.AddSingleton(options);
            _ = services.AddSingleton(resolver);
            _ = services.AddSingleton(clock);
            _ = services.AddSingleton(options.ToMeshOptions() with
            {
                StorageFormatId = storageFormatId,
                StorageFormatMismatchBackoff = options.PingInterval,
            });
            _ = services.AddSingleton<QuicMeshMetrics>();
            _ = services.AddSingleton<QuicRpcHost>();
            _ = services.AddSingleton<QuicRpcClient>();
            _ = services.AddSingleton<ClusterTopology>();
            _ = services.AddSingleton<ClusterDatabases>();
            _ = services.AddSingleton<ChangefeedPublisher>();
            _ = services.AddSingleton<StrongLockTable>();
            _ = services.AddSingleton<StrongTransactionRegistry>();
            _ = services.AddSingleton<PeerHealthMonitor>();
            _ = services.AddSingleton<DeltaReplicator>();
            _ = services.AddSingleton<InRegionRaftRouter>();
            _ = services.AddSingleton<CommitFinalizer>();
            _ = services.AddSingleton<StrongParticipant>();
            _ = services.AddSingleton<StrongVoteCollector>();
            _ = services.AddSingleton<StrongCommitRecovery>();
            _ = services.AddSingleton<StrongCoordinator>();
            _ = services.AddSingleton<AntiEntropyService>();
            _ = services.AddSingleton<ClusterCoordinator>();
            _ = services.AddSingleton<IClusterCoordinator>(static provider => provider.GetRequiredService<ClusterCoordinator>());
            _ = services.AddSingleton<ClusterComponents>();
            _ = services.AddSingleton<ClusterHostedService>();
            _ = services.AddHostedService(static provider => provider.GetRequiredService<ClusterHostedService>());
            return services;
        }
    }
}
