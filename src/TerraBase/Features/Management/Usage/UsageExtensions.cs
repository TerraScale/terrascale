using TerraBase.Features.Management.Logs;

namespace TerraBase.Features.Management.Usage;

public static class UsageExtensions
{
    public static IServiceCollection AddTerraBaseUsage(this IServiceCollection services) =>
        services
            .AddSingleton<PeerUsage>()
            .AddSingleton<UsageMeter>()
            .AddSingleton<IUsageMeter>(provider => provider.GetRequiredService<UsageMeter>())
            .AddSingleton<IUsageService, UsageService>()
            .AddSingleton<IOperationAllowance, OperationAllowance>()
            .AddSingleton<IUsageAnalytics, UsageAnalytics>()
            .AddSingleton<IRequestLogs, RequestLogRecorder>()
            .AddHostedService<UsageFlushService>();

}
