using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.CrossRegion;

namespace TerraBase.Features.Stats;

/// <summary>Registers live per-database statistics. Always on: nothing is configured.</summary>
public static class StatsExtensions
{
    /// <summary>
    /// Registers <see cref="IDatabaseStats"/>, its metrics and the service that closes a window every five seconds and
    /// publishes it on the region bus, when NATS is configured.
    /// </summary>
    public static IServiceCollection AddTerraBaseStats(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseStats>();
        services.AddSingleton<IDatabaseStats>(provider => provider.GetRequiredService<DatabaseStats>());
        services.AddSingleton<StatsMetrics>();
        services.AddHostedService(provider => new StatsService(provider.GetRequiredService<DatabaseStats>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<StatsMetrics>(), provider.GetRequiredService<IStorageEngine>(), provider.GetRequiredService<TerraBaseOptions>(),
            provider.GetService<IRegionBus>(), provider.GetRequiredService<ILogger<StatsService>>()));
        return services;
    }
}
