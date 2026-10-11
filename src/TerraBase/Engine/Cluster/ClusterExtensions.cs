using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;

namespace TerraBase.Engine.Cluster;

/// <summary>Registers the cell's membership and the leases derived from it.</summary>
public static class ClusterExtensions
{
    /// <summary>
    /// Registers <see cref="IShipperLeases"/> over the cell's membership: NATS KV when <c>nats-url</c> is set (or a test
    /// registered its own member store), otherwise the static <c>peers</c> list.
    /// </summary>
    public static IServiceCollection AddTerraBaseCluster(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Nats is { } nats)
        {
            services.TryAddSingleton<IMemberStore>(provider => new NatsMemberStore(nats, provider.GetRequiredService<ILoggerFactory>()));
        }

        return services
            .AddSingleton<IMembership>(provider => provider.GetService<IMemberStore>() is { } store
                ? new StoreMembership(store, options, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<StoreMembership>>())
                : new StaticMembership(options))
            .AddHostedService(provider => new MembershipHeartbeat(provider.GetRequiredService<IMembership>(), provider.GetRequiredService<TimeProvider>()))
            .AddSingleton<IShipperLeases, ClusterShipperLeases>();
    }
}
