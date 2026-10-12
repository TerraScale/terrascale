using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;

namespace TerraBase.Features.CrossRegion;

/// <summary>Registers cross-region replication (<c>docs/cross-region.md</c>). Always on: the regions come from the membership.</summary>
public static class CrossRegionExtensions
{
    /// <summary>
    /// The other regions' members, asked over the peer listener; the responder that answers them (<c>Wan.Ask</c>); and the
    /// puller that applies their change groups. A member without a peer listener reaches no other region.
    /// </summary>
    public static IServiceCollection AddTerraBaseCrossRegion(this IServiceCollection services)
    {
        services.TryAddSingleton<IReplicationJournal, EngineReplicationJournal>();
        services.TryAddSingleton<IRegionPeers>(provider => new GrpcRegionPeers(provider.GetRequiredService<IMembership>()));
        return services
            .AddSingleton<WanMetrics>()
            .AddSingleton<WanResponder>()
            .AddHostedService<WanPuller>();
    }
}
