using ZLogger;

namespace TerraScale.Platform.Features.Functions;

public sealed class FunctionSweepWorker(
    FunctionService functions,
    PlatformReadiness readiness,
    TimeProvider timeProvider,
    ILogger<FunctionSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset dueAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!readiness.SchemaReady)
            {
                continue;
            }

            var now = timeProvider.GetUtcNow();
            if (now < dueAt)
            {
                continue;
            }

            var outcome = await functions.SweepClosedAsync(stoppingToken);
            dueAt = outcome switch
            {
                ClosedFunctionsSwept => now,
                ClosedFunctionsIdle => now + IdleInterval,
                PlatformUnavailable unavailable => Deferred(unavailable.Message, now + IdleInterval),
            };
        }
    }

    private DateTimeOffset Deferred(string message, DateTimeOffset retryAt)
    {
        logger.ZLogWarning($"Closed project function sweep deferred: {message}");
        return retryAt;
    }
}
