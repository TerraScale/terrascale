using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;
using ZLogger;

namespace TerraScale.Authentication.Features.Observability;

/// <summary>
/// Checks storage access, the serving issuer's public signing metadata and this replica's signing-key bootstrap. The
/// check itself never writes identity state. Missing keys fail at once with <see cref="KeyRing.NoSigningKeys"/>, before
/// waiting on a bootstrap that cannot complete without them.
/// </summary>
public sealed class SigningKeyReadiness(TerraBaseStorageHealth storage, KeyRing ring, SigningKeyRotation rotation) : IStorageHealth
{
    /// <inheritdoc />
    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        var storageCheck = storage.CheckAsync(cancellationToken);
        // The read-only signing check is independent of the synthetic storage write sequence.
        await Task.WhenAll(storageCheck, ring.CheckPublicMetadataAsync(OidcIssuer.InstanceId, cancellationToken)).ConfigureAwait(false);
        var result = await storageCheck.ConfigureAwait(false);
        if (result.Healthy)
        {
            await rotation.Bootstrapped.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>One completed readiness observation; <paramref name="Error"/> only names a failure an operator must act on.</summary>
public sealed record ReadinessObservation(bool Ready, string? Region, string? Error = null);

/// <summary>
/// Evaluates readiness on a background loop five seconds after each evaluation ends, bounds each
/// evaluation to eight seconds and serves the latest result to <c>/readyz</c> without running checks. A result older
/// than fifteen seconds means the loop is stuck, so readiness fails.
/// </summary>
public sealed class ReadinessProbe(IStorageHealth health, TimeProvider clock, ILogger<ReadinessProbe> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    // The probe's conditional create and delete commit in this region only (local_durable), so a slow peer does not fail it.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private const string TimedOut = "timed out";
    private const string Stale = "readiness evaluation is stale";

    private readonly Lock gate = new();
    private ReadinessObservation? latest;
    // Monotonic: a wall-clock step backwards must not age the latest result.
    private long latestTimestamp;
    // The storage check itself, which can outlive a timed-out wait when it ignores cancellation.
    private Task? outstandingCheck;

    // Completion of the exact unwrapped task used by the no-stacking guard, including cancellation propagation.
    internal Task? OutstandingCheck
    {
        get
        {
            lock (this.gate)
            {
                return this.outstandingCheck;
            }
        }
    }

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
        while (true)
        {
            await this.ProbeAsync(new CancellationTokenSource(ProbeTimeout, clock));
            // Measured from the end of a pass: the deadline outlasts the interval, and a fixed-rate tick
            // would start the next pass before a timed-out check has unwound.
            await Task.Delay(Interval, clock, stoppingToken);
        }
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
            // Missing signing keys are reported as such while startup provisioning is pending.
            var operatorAction = exception is ApiException { Code: KeyRing.NoSigningKeysCode } ? exception.Message : null;
            observation = new ReadinessObservation(false, null, operatorAction);
            // Our own deadline cancelling the wait is the expected not-ready answer, not a fault to trace.
            var timedOut = exception is OperationCanceledException && timeout.IsCancellationRequested;
            cause = timedOut ? TimedOut : operatorAction ?? exception.GetType().FullName;
            fault = timedOut || operatorAction is not null ? null : exception;
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
