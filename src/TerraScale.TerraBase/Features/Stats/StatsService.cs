namespace TerraBase.Features.Stats;

/// <summary>Closes a statistics window every <see cref="Window"/>.</summary>
internal sealed class StatsService(DatabaseStats stats, TimeProvider time) : BackgroundService
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Window, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Leave the clock's callback thread, as the flush loops do, so a test clock advanced far ahead does not close windows inline.
            await Task.Yield();
            stats.Close(time.GetUtcNow());
        }
    }
}
