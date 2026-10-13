using Microsoft.AspNetCore.Connections;

namespace TerraBase.Hosting;

// Requests get two seconds, hosted services eight in total, then storage gets its ten-second close grace.
// Fly allows thirty seconds. Never dispose storage underneath a worker that has not returned its leases.
internal sealed class Shutdown : IDisposable
{
    internal static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan HostedBudget = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly CancellationTokenSource _drain = new();
    private readonly CancellationTokenRegistration _stopping;

    public Shutdown(IHostApplicationLifetime lifetime)
    {
        _stopping = lifetime.ApplicationStopping.Register(() => _drain.CancelAfter(DrainBudget));
    }

    internal CancellationTokenRegistration Drain(ConnectionContext connection) =>
        _drain.Token.Register(() => connection.Abort(new ConnectionAbortedException("TerraBase shutdown drain expired")));

    public void Dispose()
    {
        _stopping.Dispose();
        _drain.Dispose();
    }
}
