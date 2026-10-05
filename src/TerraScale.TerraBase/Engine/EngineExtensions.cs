using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Engine;

/// <summary>Registers the storage engine.</summary>
public static class EngineExtensions
{
    /// <summary>The key of an optional <see cref="TimeProvider"/> that clocks query deadlines in place of the system clock. Only
    /// integration tests register one: the host's clock, which tests also fake, must not move a running query's deadline.
    /// </summary>
    internal const string QueryDeadlineClock = "query-deadline";

    /// <summary>
    /// Registers the SQLite engine over <see cref="TerraBaseOptions.DataDirectory"/> as <see cref="IStorageEngine"/>,
    /// with the <see cref="IWriteStamps"/> of every write made through it.
    /// </summary>
    public static IServiceCollection AddTerraBaseEngine(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<DatabaseLifecycleCheckpoint>(_ => static (_, _) => { });
        // The engine records metrics; AddMetrics is idempotent and the host adds it too.
        services.AddMetrics();
        services.AddLogging();
        // A factory registration lets the container own, and dispose, the engine.
        services.AddSingleton(provider =>
        {
            var configured = provider.GetRequiredService<TerraBaseOptions>();
            return new SqliteStorageEngine(
                configured.DataDirectory, configured.StorageThreads, configured.StorageQueue, configured.MemoryBudget,
                provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName),
                provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), configured.ShutdownGrace, readinessCacheTtl: configured.ReadinessCacheTtl,
                checkpoint: provider.GetRequiredService<DatabaseLifecycleCheckpoint>(),
                time: provider.GetKeyedService<TimeProvider>(QueryDeadlineClock));
        });
        services.AddSingleton(provider => new StampingStorageEngine(provider.GetRequiredService<SqliteStorageEngine>()));
        services.AddSingleton<IStorageEngine>(provider => provider.GetRequiredService<StampingStorageEngine>());
        return services.AddSingleton<IWriteStamps>(provider => provider.GetRequiredService<StampingStorageEngine>());
    }
}
