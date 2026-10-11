using TerraBase.Engine;
using TerraBase.Features.Replication;

namespace TerraBase.Features.Health;

/// <summary>Local serving readiness, not certified membership or replica catch-up evidence.</summary>
public interface INodeReadiness
{
    /// <summary>
    /// Checks storage, catalog readiness and shutdown with the readiness probe's rules. Not ts-auth's signing keys: ts-auth
    /// stores its data here, so it cannot become ready before this node does; user tokens answer 401 until the keys load.
    /// </summary>
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class NodeReadiness(
    IStorageEngine engine, ICatalogReadiness catalog, IHostApplicationLifetime lifetime) : INodeReadiness
{
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!catalog.IsReady || lifetime.ApplicationStopping.IsCancellationRequested)
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

/// <summary>Registers the shared local readiness check.</summary>
public static class HealthExtensions
{
    /// <summary>Shares identical probe and pressure readiness rules.</summary>
    public static IServiceCollection AddTerraBaseHealth(this IServiceCollection services)
    {
        services.AddSingleton<INodeReadiness, NodeReadiness>();
        return services;
    }
}
