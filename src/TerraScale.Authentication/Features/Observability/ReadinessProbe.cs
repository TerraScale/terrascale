using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Observability;

public sealed class SigningKeyReadiness(TerraBaseStorageHealth storage, KeyRing ring) : IStorageHealth
{
    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        var storageCheck = storage.CheckAsync(cancellationToken);
        await Task.WhenAll(storageCheck, ring.CheckPublicMetadataAsync(OidcIssuer.InstanceId, cancellationToken));
        return await storageCheck;
    }
}

public sealed record ReadinessObservation(bool Ready, string? Region, string? Error = null);

public sealed class ReadinessProbe(AuthSchema schema, IStorageHealth health, TimeProvider clock, ILogger<ReadinessProbe> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private const string TimedOut = "timed out";

    internal const int FailuresBeforeNotReady = 3;
    private const string Stale = "readiness evaluation is stale";

    private readonly Lock gate = new();
    private ReadinessObservation? latest;
    private long latestTimestamp;
    private Task? outstandingCheck;

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

    private string? failure;

    private int failures;

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

        this.Record(Stale, null);
        return new ReadinessObservation(false, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        while (true)
        {
            await this.ProbeAsync(new CancellationTokenSource(ProbeTimeout, clock));
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
            stacked = this.outstandingCheck is { IsCompleted: false };
        }

        if (stacked)
        {
            return this.Settle(new ReadinessObservation(false, null), "an earlier check is still running", null);
        }

        try
        {
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
            var operatorAction = exception is ApiException { Code: KeyRing.NoSigningKeysCode } ? exception.Message : null;
            observation = new ReadinessObservation(false, null, operatorAction);
            var timedOut = exception is OperationCanceledException && timeout.IsCancellationRequested;
            cause = timedOut ? TimedOut : operatorAction ?? exception.GetType().FullName;
            fault = timedOut || operatorAction is not null ? null : exception;
            _ = check?.ContinueWith(static task => task.Exception, TaskScheduler.Default);
        }

        return this.Settle(observation, cause, fault);
    }

    private ReadinessObservation Settle(ReadinessObservation observation, string? cause, Exception? fault)
    {
        lock (this.gate)
        {
            this.failures = observation.Ready ? 0 : this.failures + 1;
            if (observation.Ready || observation.Error is not null || this.latest is not { Ready: true } || this.failures >= FailuresBeforeNotReady)
            {
                this.latest = observation;
            }

            observation = this.latest!;
            this.latestTimestamp = clock.GetTimestamp();
        }

        this.Record(cause, fault);
        return observation;
    }

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
            _ = logFailure;
        }
    }
}
