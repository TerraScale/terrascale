namespace TerraBase.Features.Stats;

/// <summary>Registers live per-database statistics. Always on: nothing is configured.</summary>
public static class StatsExtensions
{
    /// <summary>Registers <see cref="IDatabaseStats"/>, its metrics and the timer that closes a window every five seconds.</summary>
    public static IServiceCollection AddTerraBaseStats(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseStats>();
        services.AddSingleton<IDatabaseStats>(provider => provider.GetRequiredService<DatabaseStats>());
        services.AddSingleton<StatsMetrics>();
        services.AddHostedService(provider =>
        {
            // Resolving the metrics creates their instruments before the first window closes.
            _ = provider.GetRequiredService<StatsMetrics>();
            return new StatsService(provider.GetRequiredService<DatabaseStats>(), provider.GetRequiredService<TimeProvider>());
        });
        return services;
    }
}
