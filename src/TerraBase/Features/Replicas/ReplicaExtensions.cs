using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraBase.Features.Replicas;

public static class ReplicaExtensions
{
    /// <summary>Region replicas: revision notices as a home, replicas of other regions' databases as a replica region.</summary>
    public static IServiceCollection AddTerraBaseRegionReplicas(this IServiceCollection services)
    {
        services.AddSingleton<RegionalOwners>();
        services.AddSingleton<RegionalHandoff>();
        services.AddHostedService(provider => provider.GetRequiredService<RegionalHandoff>());
        services.TryAddSingleton<IReplicaPeers, PeersFileReplicaPeers>();
        services.TryAddSingleton<IReplicaCatalog, TenantReplicaCatalog>();
        services.TryAddSingleton<IReplicaStatus, ReplicaStatusBoard>();
        services.AddSingleton<ReplicaHolds>();
        services.AddSingleton<RegionReplication>();
        services.AddSingleton<HomeSessions>();
        services.AddSingleton<HomeMoves>();
        services.AddHostedService(provider => provider.GetRequiredService<HomeMoves>());
        services.AddSingleton<HomeLeases>();
        services.AddSingleton<IHomeWriteLease>(provider => provider.GetRequiredService<HomeLeases>());
        services.AddHostedService(provider => provider.GetRequiredService<HomeLeases>());
        return services.AddHostedService(provider => provider.GetRequiredService<RegionReplication>());
    }
}
