using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.HomePush;
using TerraBase.Features.Platform;
using TerraBase.Features.Replicas;
using TerraBase.Features.UserAuth;
using HomePushService = TerraBase.Features.HomePush.HomePush;

namespace TerraBase.Features.Health;

public interface INodeReadiness
{
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class NodeReadiness(
    IStorageEngine engine, IHostApplicationLifetime lifetime, ICatalogGate gate, PlatformSeedProgress platform,
    RegionalOwners owners, TerraBaseOptions options,
    HomePushService push, HomeLeases leases, IIssuerKeys issuerKeys) : INodeReadiness
{
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsOpen || !platform.Ready || lifetime.ApplicationStopping.IsCancellationRequested
            || options.AuthIssuer is not null && issuerKeys.Keys is null
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
