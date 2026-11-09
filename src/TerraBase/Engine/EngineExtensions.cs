using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Hosting;

namespace TerraBase.Engine;

public static class EngineExtensions
{
    internal const string QueryDeadlineClock = "query-deadline";

    internal const string DatabaseMasterKey = "database-master-key";

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
        services.AddMetrics();
        services.TryAddSingleton<IDiskBudget, DiskBudget>();
        services.AddTerraBaseMemory(options);
        services.AddLogging();
        services.AddSingleton(provider =>
        {
            var configured = provider.GetRequiredService<TerraBaseOptions>();
            return new TursoStorageEngine(
                new StorageEngineOptions(
                    configured.DataDirectory, configured.StorageThreads, configured.StorageQueue, configured.MemoryBudget,
                    configured.ShutdownGrace, ReadinessCacheTtl: configured.ReadinessCacheTtl, QueryBudget: configured.QueryBudget,
                    Time: provider.GetKeyedService<TimeProvider>(QueryDeadlineClock), BackupTime: provider.GetService<TimeProvider>()),
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<TursoStorageEngine>>(), provider.GetRequiredKeyedService<IMasterKey>(DatabaseMasterKey),
                checkpoint: provider.GetRequiredService<DatabaseLifecycleCheckpoint>(),
                schemaMemory: provider.GetRequiredService<IMemoryBudget>(),
                nativeTemporaryStorage: provider.GetRequiredService<INativeTemporaryStorage>(),
                diskBudget: provider.GetRequiredService<IDiskBudget>());
        });
        services.AddSingleton<IOrphanDatabaseFiles>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IWalBackup>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton(provider => provider.GetRequiredService<TursoStorageEngine>().Leases);
        services.AddSingleton<IRecoverySnapshotReader>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IHealthyRewrite>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IDatabaseRevisions>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton<IDatabaseReplicas, TursoReplicas>();
        services.AddSingleton<IDatabaseRewind>(provider => provider.GetRequiredService<TursoStorageEngine>());
        services.AddSingleton(provider => provider.GetRequiredService<TursoStorageEngine>().Fences);
        services.AddSingleton<IStorageCopyFaults>(provider => provider.GetRequiredService<TursoStorageEngine>().Ledger);
        services.AddSingleton<IShapeStorage>(provider => new TursoShapeStorage(provider.GetRequiredService<TursoStorageEngine>()));
        services.AddSingleton<IShapeSnapshotTransactions>(provider => new TursoShapeSnapshotTransactions(provider.GetRequiredService<TursoStorageEngine>()));
        return services.AddSingleton<IStorageEngine>(provider => provider.GetRequiredService<TursoStorageEngine>());
    }
}
