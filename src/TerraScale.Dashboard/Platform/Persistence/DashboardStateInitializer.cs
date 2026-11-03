namespace TerraScale.Dashboard.Platform.Persistence
{
    // Traffic admission follows the delivered secrets and the fixed schema. A missing or invalid secret stops startup;
    // this service never invents one. TerraBase being unreachable or refusing never stops it: the schema initializes in
    // the background, retrying with jittered backoff, and until it has, /readyz is not ready and every other request
    // answers 503 (no fallback store, no writes).
    public sealed class DashboardStateInitializer(IDashboardIdentity identity, IDashboardState state, TimeProvider time,
        ILogger<DashboardStateInitializer> logger) : BackgroundService
    {
        private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(30);
        // The first attempt runs inside startup, bounded, so a healthy start admits traffic as soon as it listens.
        private static readonly TimeSpan StartupAttempt = TimeSpan.FromSeconds(10);
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Initialized => ready.Task.IsCompleted;

        // Completes once the fixed schema is initialized, whether at startup or on a later retry.
        public Task WhenInitialized => ready.Task;

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await identity.ReadAsync(cancellationToken);
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attempt.CancelAfter(StartupAttempt);
                try { await state.InitializeAsync(attempt.Token); ready.TrySetResult(); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.DashboardStateIsNotInitializedAt(error.GetType().Name, error.Message);
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
                    await state.InitializeAsync(stoppingToken);
                    ready.TrySetResult();
                    logger.DashboardStateInitializedAfterAttempts(attempt + 1);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.DashboardStateIsNotInitializedAfter(attempt + 1, error.GetType().Name, error.Message);
                }
            }
        }

        // Doubles from one second to 30, and waits a random 50-100% of that so replicas restarted together spread out.
        public static TimeSpan Backoff(int attempt, double jitter)
        {
            return TimeSpan.FromTicks((long)(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(attempt, 10)) * (0.5 + 0.5 * Math.Clamp(jitter, 0, 1))));
        }
    }
}
