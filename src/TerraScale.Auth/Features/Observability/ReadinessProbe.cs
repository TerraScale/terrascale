using TerraScale.Auth.Features.Oidc;
using TerraScale.Auth.Features.SigningKeys;
using TerraScale.Auth.Store;
using ZLogger;

namespace TerraScale.Auth.Features.Observability;

/// <summary>
/// Checks storage access, this replica's signing-ring bootstrap and the serving issuer's public
/// signing metadata. The check itself never writes identity state. The public-metadata read is
/// cached for five minutes after it succeeds; a failure is never cached.
/// </summary>
public sealed class SigningKeyReadiness(TerraBaseStorageHealth storage, KeyRing ring, SigningKeyRotation rotation, TimeProvider clock) : IStorageHealth
{
    private static readonly TimeSpan VaultCacheTtl = TimeSpan.FromMinutes(5);

    // Only the readiness loop calls CheckAsync, so no lock guards this.
    private long? vaultVerifiedAt;

    /// <inheritdoc />
    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        var result = await storage.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (result.Healthy)
        {
            await rotation.Bootstrapped.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (this.vaultVerifiedAt is not { } verified || clock.GetElapsedTime(verified) >= VaultCacheTtl)
            {
                await ring.CheckPublicMetadataAsync(OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false);
                this.vaultVerifiedAt = clock.GetTimestamp();
            }
        }

        return result;
    }
}

/// <summary>One completed readiness observation.</summary>
public sealed record ReadinessObservation(bool Ready, string? Region);

/// <summary>
/// Evaluates readiness on a background loop every five seconds, bounds each evaluation to two
/// seconds and serves the latest result to <c>/readyz</c> without running checks. A result older
/// than fifteen seconds means the loop is stuck, so readiness fails.
/// </summary>
public sealed class ReadinessProbe(IStorageHealth health, TimeProvider clock, ILogger<ReadinessProbe> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private const string TimedOut = "timed out";
    private const string Stale = "readiness evaluation is stale";

    private readonly Lock gate = new();
    private ReadinessObservation? latest;
    // Monotonic: a wall-clock step backwards must not age the latest result.
    private long latestTimestamp;
    // The storage check itself, which can outlive a timed-out wait when it ignores cancellation.
    private Task? outstandingCheck;
    // Why the latest observation failed; null while ready. Only the logging transitions read it.
    private string? failure;

    /// <summary>
    /// Returns the latest evaluation. Not ready before the first evaluation completes, and not ready
    /// once that evaluation is older than fifteen seconds.
    /// </summary>
    public ReadinessObservation Latest()
    {
        ReadinessObservation? observation;
        bool stale;
        lock (this.gate)
        {
            observation = this.latest;
            stale = observation is not null && clock.GetElapsedTime(this.latestTimestamp) > StaleAfter;
        }

        if (observation is null)
        {
            return new ReadinessObservation(false, null);
        }

        if (!stale)
        {
            return observation;
        }

        // Logged here because a stuck loop cannot log its own failure.
        this.Record(Stale, null);
        return new ReadinessObservation(false, null);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            await this.ProbeAsync(new CancellationTokenSource(ProbeTimeout, clock));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<ReadinessObservation> ProbeAsync(CancellationTokenSource deadline)
    {
        using var timeout = deadline;
        ReadinessObservation observation;
        string? cause = null;
        Exception? fault = null;
        Task<StorageHealthResult>? check = null;
        bool stacked;
        lock (this.gate)
        {
            // Never stack storage work: while an earlier check is still running, stay not ready.
            stacked = this.outstandingCheck is { IsCompleted: false };
        }

        if (stacked)
        {
            return this.Settle(new ReadinessObservation(false, null), "an earlier check is still running", null);
        }

        // One deadline cancels both the storage call and the wait for it, so a timed-out
        // wait always means the check was asked to stop.
        try
        {
            // Task.Run bounds even an implementation that blocks before returning its task. A
            // worker that only starts after the deadline never begins storage work at all.
            check = Task.Run(
                () =>
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    return health.CheckAsync(timeout.Token);
                },
                timeout.Token);
            lock (this.gate)
            {
                this.outstandingCheck = check;
            }

            var result = await check.WaitAsync(timeout.Token);
            // A result that arrives after the deadline (a late-resumed worker, say) is stale:
            // readiness was already decided not-ready for this pass, so it must not count as healthy.
            if (timeout.IsCancellationRequested)
            {
                observation = new ReadinessObservation(false, null);
                cause = TimedOut;
            }
            else
            {
                observation = new ReadinessObservation(result.Healthy, result.Region);
                cause = result.Healthy ? null : "storage reported unhealthy";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            observation = new ReadinessObservation(false, null);
            // Our own deadline cancelling the wait is the expected not-ready answer, not a fault to trace.
            var timedOut = exception is OperationCanceledException && timeout.IsCancellationRequested;
            cause = timedOut ? TimedOut : exception.GetType().FullName;
            fault = timedOut ? null : exception;
            // Observe a late failure so it is not reported as unobserved.
            _ = check?.ContinueWith(static task => task.Exception, TaskScheduler.Default);
        }

        return this.Settle(observation, cause, fault);
    }

    // Publishes the observation, then logs the transition.
    private ReadinessObservation Settle(ReadinessObservation observation, string? cause, Exception? fault)
    {
        lock (this.gate)
        {
            this.latest = observation;
            this.latestTimestamp = clock.GetTimestamp();
        }

        this.Record(cause, fault);
        return observation;
    }

    // Logs only transitions: the first failure or a new cause warns, repeats stay at Debug, and the
    // first success after a failure is reported once.
    private void Record(string? cause, Exception? fault)
    {
        string? previous;
        lock (this.gate)
        {
            previous = this.failure;
            this.failure = cause;
        }

        try
        {
            if (cause is null)
            {
                if (previous is not null)
                {
                    logger.ZLogInformation($"Readiness check recovered");
                }
            }
            else if (cause != previous)
            {
                logger.ZLogWarning(fault, $"Readiness check failed: {cause:@Cause}");
            }
            else
            {
                logger.ZLogDebug($"Readiness check still failing: {cause:@Cause}");
            }
        }
        catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
        {
            // Logging is best effort: a failing provider must not stop the readiness loop.
            _ = logFailure;
        }
    }
}
