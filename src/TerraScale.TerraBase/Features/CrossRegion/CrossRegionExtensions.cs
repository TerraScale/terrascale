using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Replication;

namespace TerraBase.Features.CrossRegion;

/// <summary>Registers cross-region replication (<c>docs/cross-region.md</c>).</summary>
public static class CrossRegionExtensions
{
    /// <summary>
    /// The region bus whenever <c>nats-url</c> is set (live statistics use it too). When <c>wan-regions</c> is set: the
    /// responder that answers other regions and the puller that applies their change groups. Nothing otherwise.
    /// </summary>
    public static IServiceCollection AddTerraBaseCrossRegion(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Nats is { } nats)
        {
            services.TryAddSingleton<IRegionBus>(provider => new NatsRegionBus(nats, provider.GetRequiredService<ILoggerFactory>()));
        }

        if (options.Wan is null)
        {
            return services;
        }

        services.TryAddSingleton<IReplicationJournal, EngineReplicationJournal>();
        return services
            .AddSingleton<WanMetrics>()
            .AddHostedService<WanResponder>()
            .AddHostedService<WanPuller>();
    }
}
