namespace TerraScale.TerraBase.Features.Management.Usage;

/// <summary>Registers usage metering and reporting.</summary>
public static class UsageExtensions
{
    /// <summary>Registers <see cref="IUsageMeter"/>, <see cref="IUsageService"/> and the background flush.</summary>
    public static IServiceCollection AddTerraBaseUsage(this IServiceCollection services) =>
        services
            .AddSingleton<UsageMeter>()
            .AddSingleton<IUsageMeter>(provider => provider.GetRequiredService<UsageMeter>())
            .AddSingleton<IUsageService, UsageService>()
            .AddHostedService<UsageFlushService>();
}
