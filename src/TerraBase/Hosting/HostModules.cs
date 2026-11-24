using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Continuity;
using TerraBase.Engine;
using TerraBase.Features.InternalApi;
using TerraBase.Storage;
using TerraScale.QuicRpc;

namespace TerraBase.Hosting;

internal static class HostModules
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTerraBaseNode(PreparedStartup prepared)
        {
            ArgumentNullException.ThrowIfNull(prepared);
            var options = prepared.Options;
            var deltaColumns = new DeltaColumnRegistry();
            SystemDatabase.RegisterDeltaColumns(deltaColumns);
            var identity = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(options.Region, "/", options.MachineId)));
            var workerId = DeriveSnowflakeWorkerId(prepared.Cluster, identity);
            var storage = new StorageEngineOptions(
                options.StorageDirectory,
                prepared.Cluster.StorageOrigin,
                BinaryPrimitives.ReadUInt16LittleEndian(identity),
                workerId,
                deltaColumns)
            {
                ReaderCount = Math.Clamp(options.StorageThreads, 1, 64),
                BookmarkWaitTimeout = options.QueryBudget,
                StatementTimeout = options.QueryBudget,
            };
            var engine = new SqliteStorageEngine(storage, TimeProvider.System);
            services.AddSingleton(deltaColumns);
            services.AddSingleton(storage);
            services.AddSingleton(prepared.Keyring);
            services.AddSingleton(_ => engine);
            services.AddSingleton<ISqlStorageEngine>(static provider => provider.GetRequiredService<SqliteStorageEngine>());
            services.AddSingleton<TenantDatabases>();
            services.AddSingleton<SystemDatabase>();
            return services.AddClusterAndContinuity(prepared.Cluster, prepared.Continuity, engine.Clock);
        }

        public IServiceCollection AddClusterAndContinuity(ClusterOptions cluster, ContinuityOptions continuity, HlcClock clock)
        {
            services.AddSingleton<ClusterSessions>();
            services.AddTerraBaseCluster(cluster, static provider => provider.GetRequiredService<ClusterSessions>().ResolveAsync, clock);
            services.AddSingleton<ArchiveCatchUp>(static provider => provider.GetRequiredService<ClusterSessions>().CatchUpFromArchiveAsync);
            services.AddSingleton<IContinuityDatabaseCatalog>(static provider => provider.GetRequiredService<ClusterSessions>());
            services.AddSingleton<IQuicEndpoints, InternalSqlQuicEndpoint>();
            services.AddSingleton<IQuicEndpoints, RewindDatabaseQuicEndpoint>();
            services.AddSingleton<SystemBootstrap>();
            services.AddHostedService(static provider => provider.GetRequiredService<SystemBootstrap>());
            return services.AddTerraBaseContinuity(continuity);
        }
    }

    private static byte DeriveSnowflakeWorkerId(ClusterOptions cluster, byte[] identity)
    {
        if (cluster.Peers.Count == 0)
        {
            return (byte)(identity[2] % (SnowflakeGenerator.MaxWorkerId + 1));
        }

        var nodes = cluster.Peers
            .Select(static peer => peer.NodeId)
            .Append(cluster.NodeId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var rank = nodes.IndexOf(cluster.NodeId);
        return (byte)(rank >= 0 ? rank % (SnowflakeGenerator.MaxWorkerId + 1) : identity[2] % (SnowflakeGenerator.MaxWorkerId + 1));
    }
}
