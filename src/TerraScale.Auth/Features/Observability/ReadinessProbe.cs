using TerraScale.Auth.Store;

namespace TerraScale.Auth.Features.Observability;

/// <summary>One completed readiness observation.</summary>
public sealed record ReadinessObservation(bool Ready, string? Region);

/// <summary>
/// Coalesces concurrent <c>/readyz</c> probes into one storage round trip, caches the result
/// for one second and bounds each wait to two seconds.
/// The cache only shortens repeat probes; it never turns a failed check into success.
/// </summary>
public sealed partial class ReadinessProbe(IStorageHealth health, TimeProvider clock, ILogger<ReadinessProbe> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock gate = new();
    private ReadinessObservation? latest;
    // Monotonic: a wall-clock step backwards must not extend the cache window.
    private long latestTimestamp;
    private Task<ReadinessObservation>? inFlight;
    // The storage check itself, which can outlive a timed-out wait when it ignores cancellation.
    private Task? outstandingCheck;

    /// <summary>
    /// Returns a fresh-enough observation, joining any probe already running. Every caller
    /// waits at most two seconds from entry, however late the shared probe gets scheduled.
    /// </summary>
    public async Task<ReadinessObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        Task<ReadinessObservation> probe;
        lock (this.gate)
        {
            if (this.latest is { } cached && clock.GetElapsedTime(this.latestTimestamp) < CacheTtl)
            {
                return cached;
            }

            if (this.inFlight is null)
            {
                // The deadline starts now, not when the thread pool gets to the probe.
                // Task.Run keeps the probe from completing inside this lock: a synchronously
                // finished probe would otherwise clear inFlight before it is assigned and
                // leave a completed task cached forever.
                var deadline = new CancellationTokenSource(ProbeTimeout, clock);
                this.inFlight = Task.Run(() => this.ProbeAsync(deadline), CancellationToken.None);
            }

            probe = this.inFlight;
        }

        try
        {
            // A caller that disconnects stops waiting; the shared probe still completes for others.
            return await probe.WaitAsync(ProbeTimeout, clock, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new ReadinessObservation(false, null);
        }
    }

    private async Task<ReadinessObservation> ProbeAsync(CancellationTokenSource deadline)
    {
        using var timeout = deadline;
        ReadinessObservation observation;
        Task<StorageHealthResult>? check = null;
        lock (this.gate)
        {
            // Never stack storage work: while an earlier check is still running, stay not ready.
            if (this.outstandingCheck is { IsCompleted: false })
            {
                observation = new ReadinessObservation(false, null);
                this.Publish(observation);
                return observation;
            }
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
            // callers were already told not-ready, so it must not be cached as healthy.
            observation = timeout.IsCancellationRequested
                ? new ReadinessObservation(false, null)
                : new ReadinessObservation(result.Healthy, result.Region);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try
            {
                LogProbeFailed(logger, exception);
            }
            catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
            {
                // Logging is best effort: a failing provider must not leave the shared probe
                // faulted, which would turn every later /readyz into a 500.
                _ = logFailure;
            }
            observation = new ReadinessObservation(false, null);
            // Observe a late failure so it is not reported as unobserved.
            _ = check?.ContinueWith(static task => task.Exception, TaskScheduler.Default);
        }

        lock (this.gate)
        {
            this.Publish(observation);
        }

        return observation;
    }

    // Caller holds the gate.
    private void Publish(ReadinessObservation observation)
    {
        this.latest = observation;
        this.latestTimestamp = clock.GetTimestamp();
        this.inFlight = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Readiness check failed")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);
}
