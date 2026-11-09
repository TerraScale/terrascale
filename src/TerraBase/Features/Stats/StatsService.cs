namespace TerraBase.Features.Stats;

internal sealed class StatsService(DatabaseStats stats, TimeProvider time) : BackgroundService
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Window, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await Task.Yield();
            _ = stats.Close(time.GetUtcNow());
        }
    }
}
