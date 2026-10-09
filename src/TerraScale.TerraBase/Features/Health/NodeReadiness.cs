using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Health;

/// <summary>Local serving readiness, not certified membership or replica catch-up evidence.</summary>
public interface INodeReadiness
{
    /// <summary>Checks storage, issuer keys, catalog readiness and shutdown with the readiness probe's rules.</summary>
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class NodeReadiness(
    IStorageEngine engine, IIssuerKeys issuerKeys, ICatalogReadiness catalog, IHostApplicationLifetime lifetime) : INodeReadiness
{
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!issuerKeys.IsReady || !catalog.IsReady || lifetime.ApplicationStopping.IsCancellationRequested)
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
