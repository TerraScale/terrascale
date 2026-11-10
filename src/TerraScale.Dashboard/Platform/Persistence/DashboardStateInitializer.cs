using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public sealed class DashboardStateInitializer(IDashboardIdentity identity, IDbContextFactory<DashboardDbContext> storage, TimeProvider time,
        ILogger<DashboardStateInitializer> logger) : BackgroundService
    {
        private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartupAttempt = TimeSpan.FromSeconds(10);
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Initialized => ready.Task.IsCompleted;

        public Task WhenInitialized => ready.Task;

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await identity.ReadAsync(cancellationToken);
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attempt.CancelAfter(StartupAttempt);
                try { await InitializeAsync(attempt.Token); ready.TrySetResult(); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.DashboardStateIsNotInitializedAt(error.GetType().Name);
                }
            }
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (Initialized)
            {
                return;
            }

            for (var attempt = 1; ; attempt++)
            {
                try { await Task.Delay(Backoff(attempt - 1, Random.Shared.NextDouble()), time, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                try
                {
                    await InitializeAsync(stoppingToken);
                    ready.TrySetResult();
                    logger.DashboardStateInitializedAfterAttempts(attempt + 1);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.DashboardStateIsNotInitializedAfter(attempt + 1, error.GetType().Name);
                }
            }
        }

        private static TimeSpan Backoff(int attempt, double jitter)
        {
            return TimeSpan.FromTicks((long)(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(attempt, 10)) * (0.5 + 0.5 * Math.Clamp(jitter, 0, 1))));
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
        }
    }
}
