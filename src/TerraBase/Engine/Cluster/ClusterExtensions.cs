using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>Registers the cell's membership and the leases derived from it.</summary>
public static class ClusterExtensions
{
    /// <summary>
    /// Registers <see cref="IShipperLeases"/> over a test-supplied directory, otherwise this member alone.
    /// </summary>
    public static IServiceCollection AddTerraBaseCluster(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services
            .AddSingleton<IMembership>(provider => provider.GetService<IMemberDirectory>() is { } directory
                ? new DirectoryMembership(directory, provider.GetRequiredService<TerraBaseOptions>(), provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<DirectoryMembership>>())
                : new StaticMembership(provider.GetRequiredService<TerraBaseOptions>()))
            .AddHostedService(provider => new MembershipHeartbeat(provider.GetRequiredService<IMembership>(), provider.GetRequiredService<TimeProvider>()))
            .AddSingleton<IShipperLeases, ClusterShipperLeases>();
    }
}
