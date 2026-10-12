namespace TerraScale.Dashboard.Platform.Persistence;

// Traffic admission follows the fixed schema's creation. The Vault identity
// must already name an admitted project; this service never invents authority.
public sealed class DashboardStateInitializer(IDashboardState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => state.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
