using TerraBase.Hosting;

namespace TerraBase.Features.Management.Usage;

/// <summary>Registers usage metering and reporting.</summary>
public static class UsageExtensions
{
    /// <summary>Registers <see cref="IUsageMeter"/>, <see cref="IUsageService"/>, the background flush and the <c>CatalogUsage</c> service.</summary>
    public static IServiceCollection AddTerraBaseUsage(this IServiceCollection services) =>
        services
            .AddSingleton<UsageMeter>()
            .AddSingleton<IUsageMeter>(provider => provider.GetRequiredService<UsageMeter>())
            .AddSingleton<IUsageService, UsageService>()
            .AddSingleton<IUsageAnalytics, UsageAnalytics>()
            .AddSingleton<UsageReceiver>()
            .AddHostedService<UsageFlushService>();

    /// <summary>Maps the <c>CatalogUsage</c> service, which receives catalog followers' counts, onto the internal listener.</summary>
    public static WebApplication MapTerraBaseUsage(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGrpcService<UsageReceiver>().WithMetadata(ListenerMetadata.Internal);
        return app;
    }
}
