using TerraBase.Features.Management.Logs;

namespace TerraBase.Features.Management.Usage;

/// <summary>Registers usage metering and reporting.</summary>
public static class UsageExtensions
{
    /// <summary>Registers <see cref="IUsageMeter"/>, <see cref="IUsageService"/>, <see cref="IOperationAllowance"/> and the background flush.</summary>
    public static IServiceCollection AddTerraBaseUsage(this IServiceCollection services) =>
        services
            .AddSingleton<UsageMeter>()
            .AddSingleton<IUsageMeter>(provider => provider.GetRequiredService<UsageMeter>())
            .AddSingleton<IUsageService, UsageService>()
            .AddSingleton<IOperationAllowance, OperationAllowance>()
            .AddSingleton<IUsageAnalytics, UsageAnalytics>()
            .AddSingleton<IRequestLogs, RequestLogRecorder>()
            .AddHostedService<UsageFlushService>();

}
