using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine.Sqlite;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Engine;

/// <summary>Registers the storage engine.</summary>
public static class EngineExtensions
{
    /// <summary>
    /// The key of an optional <see cref="TimeProvider"/> that clocks query deadlines in place of the system clock. Only
    /// integration tests register one: the host's clock, which tests also fake, must not move a running query's deadline.
    /// </summary>
    internal const string QueryDeadlineClock = "query-deadline";

    /// <summary>Registers the SQLite engine over <see cref="TerraBaseOptions.DataDirectory"/> as <see cref="IStorageEngine"/>.</summary>
    public static IServiceCollection AddTerraBaseEngine(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton<DatabaseLifecycleCheckpoint>(_ => static (_, _) => { });
        // A factory registration lets the container own, and dispose, the engine.
        return services.AddSingleton<IStorageEngine>(provider => new SqliteStorageEngine(
            options.DataDirectory, options.ShutdownGrace, provider.GetRequiredService<ILogger<SqliteStorageEngine>>(), options.ReadinessCacheTtl,
            provider.GetRequiredService<DatabaseLifecycleCheckpoint>(), provider.GetKeyedService<TimeProvider>(QueryDeadlineClock)));
    }
}
