using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Features.Shapes;
using TerraBase.Hosting;

namespace TerraBase.Engine;

/// <summary>Registers the storage engine.</summary>
public static class EngineExtensions
{
    /// <summary>The key of an optional <see cref="TimeProvider"/> that clocks query deadlines, and how long a write waited for its
    /// database's writer, in place of the system clock. Only integration tests register one: the host's clock, which tests
    /// also fake, must not move a running query's deadline.
    /// </summary>
    internal const string QueryDeadlineClock = "query-deadline";

    // Database page keys and backup envelope keys share production custody, with separate test-fake boundaries.
    internal const string DatabaseMasterKey = "database-master-key";

    /// <summary>
    /// Registers the Turso engine over <see cref="TerraBaseOptions.DataDirectory"/> as <see cref="IStorageEngine"/>, with the
    /// <see cref="IWriteStamps"/> of every write made through it.
    /// </summary>
    public static IServiceCollection AddTerraBaseEngine(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<INativeTemporaryStorage, NativeTemporaryStorage>();
        services.TryAddKeyedSingleton<IMasterKey>(DatabaseMasterKey, (provider, _) =>
        {
            var configured = provider.GetRequiredService<TerraBaseOptions>();
            return new MasterKey(configured.DatabaseMasterKey ?? configured.Backup?.MasterKey
                ?? throw new TerraBaseConfigurationException("master-key is required for database encryption"));
        });
        services.TryAddSingleton<DatabaseLifecycleCheckpoint>(_ => static (_, _) => { });
        // The engine records metrics; AddMetrics is idempotent and the host adds it too.
        services.AddMetrics();
        services.TryAddSingleton<IDiskBudget, DiskBudget>();
        services.AddTerraBaseMemory(options);
        services.AddLogging();
        // A factory registration lets the container own, and dispose, the engine.
        services.AddSingleton(provider =>
        {
            var configured = provider.GetRequiredService<TerraBaseOptions>();
            return new TursoStorageEngine(
                configured.DataDirectory, configured.StorageThreads, configured.StorageQueue, configured.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(DatabaseMasterKey), configured.ShutdownGrace, readinessCacheTtl: configured.ReadinessCacheTtl,
                checkpoint: provider.GetRequiredService<DatabaseLifecycleCheckpoint>(),
                time: provider.GetKeyedService<TimeProvider>(QueryDeadlineClock), queryBudget: configured.QueryBudget,
                backupTime: provider.GetService<TimeProvider>(),
                schemaMemory: provider.GetRequiredService<IMemoryBudget>(), replicaOwner: configured.MachineId,
                nativeTemporaryStorage: provider.GetRequiredService<INativeTemporaryStorage>(),
                diskBudget: provider.GetRequiredService<IDiskBudget>())
            {
                RequiresRemoteProof = provider.GetService<IMemberDirectory>() is not null,
            };
        });
        services.AddSingleton<IOrphanDatabaseFiles>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IWalBackup>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IRecoverySnapshotReader>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IRebuildStorage>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IHealthyRewrite>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IRebuildLedger>(provider => provider.GetRequiredService<TursoStorageEngine>().Ledger);
        services.AddSingleton<IRebuildRecovery>(provider => provider.GetRequiredService<TursoStorageEngine>().Ledger);
        services.AddSingleton<IReplicaCopyFaults>(provider => provider.GetRequiredService<TursoStorageEngine>().Ledger);
        services.AddSingleton<IRebuildRepair>(provider => provider.GetRequiredService<TursoStorageEngine>().Ledger);
        services.AddSingleton(provider => provider.GetRequiredService<TursoStorageEngine>().Member);
        // The peer streams' source: the CDC tailer of every database file.
        services.AddSingleton(provider => new TursoChangeGroupSource(
            provider.GetRequiredService<TursoStorageEngine>(), provider.GetRequiredService<IMeterFactory>(), provider.GetRequiredService<ILogger<TursoChangeGroupSource>>(),
            provider.GetService<IRemoteDurability>));
        services.AddSingleton<FaultAwareReplicaSource>();
        services.AddSingleton<IChangeGroupSource>(provider => provider.GetRequiredService<FaultAwareReplicaSource>());
        services.AddSingleton<IReplicaStates>(provider => provider.GetRequiredService<FaultAwareReplicaSource>());
        services.AddSingleton<IReplicaSnapshotImporter, TursoReplicaSnapshotImporter>();
        services.AddSingleton<IReplicaSnapshotSource>(provider => provider.GetRequiredService<FaultAwareReplicaSource>());
        services.AddHostedService(provider => provider.GetRequiredService<TursoChangeGroupSource>());
        // The changes feed's 7-day retention.
        services.AddSingleton(provider => new FeedRetention(provider.GetRequiredService<TursoStorageEngine>(), provider.GetRequiredService<ILogger<FeedRetention>>()));
        services.AddHostedService(provider => provider.GetRequiredService<FeedRetention>());
        // Shape sealing and snapshots open their derived-data transactions through the Turso engine.
        services.AddSingleton<IShapeStorage>(provider => new TursoShapeStorage(provider.GetRequiredService<TursoStorageEngine>()));
        services.AddSingleton<IShapeSnapshotTransactions>(provider => new TursoShapeSnapshotTransactions(provider.GetRequiredService<TursoStorageEngine>()));
        services.AddSingleton<IShapeCleanupStore>(provider => new TursoShapeCleanupStore(provider.GetRequiredService<TursoStorageEngine>()));
        services.AddSingleton(provider => new StampingStorageEngine(provider.GetRequiredService<TursoStorageEngine>()));
        services.AddSingleton<IStorageEngine>(provider => provider.GetRequiredService<StampingStorageEngine>());
        return services.AddSingleton<IWriteStamps>(provider => provider.GetRequiredService<StampingStorageEngine>());
    }
}
