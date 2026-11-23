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
            var storage = new StorageEngineOptions(
                options.StorageDirectory,
                prepared.Cluster.StorageOrigin,
                BinaryPrimitives.ReadUInt16LittleEndian(identity),
                (byte)(identity[2] % (SnowflakeGenerator.MaxWorkerId + 1)),
                deltaColumns)
            {
                ReaderCount = Math.Clamp(options.StorageThreads, 1, 64),
                BookmarkWaitTimeout = options.QueryBudget,
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
            services.AddSingleton<IContinuityDatabaseCatalog>(static provider => provider.GetRequiredService<ClusterSessions>());
            services.AddSingleton<IQuicEndpoints, InternalSqlQuicEndpoint>();
            services.AddSingleton<SystemBootstrap>();
            services.AddHostedService(static provider => provider.GetRequiredService<SystemBootstrap>());
            return services.AddTerraBaseContinuity(continuity);
        }
    }
}
