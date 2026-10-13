namespace TerraScale.Dashboard.Platform.Persistence;

// Traffic admission follows the delivered secrets and the fixed schema. A
// missing secret or seed stops startup; this service never invents either.
public sealed class DashboardStateInitializer(IDashboardIdentity identity, IDashboardState state) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await identity.ReadAsync(cancellationToken);
        await state.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
