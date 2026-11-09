using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.HomePush;
using TerraBase.Features.Replicas;
using HomePushService = TerraBase.Features.HomePush.HomePush;

namespace TerraBase.Features.Health;

public interface INodeReadiness
{
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class NodeReadiness(
    IStorageEngine engine, IHostApplicationLifetime lifetime, ICatalogGate gate,
    RegionalOwners owners, TerraBaseOptions options,
    HomePushService push, HomeLeases leases) : INodeReadiness
{
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsOpen || lifetime.ApplicationStopping.IsCancellationRequested
            || options is { NatsUrl: not null, PeersFile: not null }
                && (owners.Find(options.Region) is null || !push.Started || !leases.Ready))
        {
            return false;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            return await engine.IsReadyAsync(stopping.Token).ConfigureAwait(false)
                && !lifetime.ApplicationStopping.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return false;
        }
    }
}
public static class HealthExtensions
{
    public static IServiceCollection AddTerraBaseHealth(this IServiceCollection services)
    {
        services.AddSingleton<INodeReadiness, NodeReadiness>();
        return services;
    }
}
