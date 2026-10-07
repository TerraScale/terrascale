using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Replication;

namespace TerraBase.Features.CrossRegion;

/// <summary>Registers cross-region replication (<c>docs/cross-region.md</c>).</summary>
public static class CrossRegionExtensions
{
    /// <summary>
    /// When <c>wan-regions</c> is set: the responder that answers other regions and the puller that applies their change
    /// groups, over NATS (or the <see cref="IRegionBus"/> a test registered). Nothing otherwise.
    /// </summary>
    public static IServiceCollection AddTerraBaseCrossRegion(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Wan is null)
        {
            return services;
        }

        services.TryAddSingleton<IRegionBus>(provider => new NatsRegionBus(options.Nats!, provider.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<IReplicationJournal, EngineReplicationJournal>();
        return services
            .AddSingleton<WanMetrics>()
            .AddHostedService<WanResponder>()
            .AddHostedService<WanPuller>();
    }
}
