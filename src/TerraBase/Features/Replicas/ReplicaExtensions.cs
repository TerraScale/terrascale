using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraBase.Features.Replicas;

public static class ReplicaExtensions
{
    /// <summary>Region replicas: revision notices as a home, replicas of other regions' databases as a replica region.</summary>
    public static IServiceCollection AddTerraBaseRegionReplicas(this IServiceCollection services)
    {
        services.TryAddSingleton<IReplicaPeers, PeersFileReplicaPeers>();
        services.TryAddSingleton<IReplicaCatalog, TenantReplicaCatalog>();
        services.TryAddSingleton<IReplicaStatus, ReplicaStatusBoard>();
        services.AddSingleton<RegionReplication>();
        services.AddSingleton<HomeSessions>();
        return services.AddHostedService(provider => provider.GetRequiredService<RegionReplication>());
    }
}
