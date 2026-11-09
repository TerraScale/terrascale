namespace TerraBase.Features.Stats;

public static class StatsExtensions
{
    public static IServiceCollection AddTerraBaseStats(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseStats>();
        services.AddSingleton<IDatabaseStats>(provider => provider.GetRequiredService<DatabaseStats>());
        services.AddSingleton<StatsMetrics>();
        services.AddHostedService(provider =>
        {
            // The metrics observe the last closed window from the start.
            _ = provider.GetRequiredService<StatsMetrics>();
            return new StatsService(provider.GetRequiredService<DatabaseStats>(), provider.GetRequiredService<TimeProvider>());
        });
        return services;
    }
}
