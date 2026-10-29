using TerraBase.Engine;
using TerraBase.Features.Catalog;

namespace TerraBase.Features.Health;
public interface INodeReadiness
{
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class NodeReadiness(
    IStorageEngine engine, IHostApplicationLifetime lifetime, ICatalogGate gate) : INodeReadiness
{
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsOpen || lifetime.ApplicationStopping.IsCancellationRequested)
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
