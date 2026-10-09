using TerraScale.Auth.Features.Oidc;

namespace TerraScale.Auth.Features.SigningKeys;

/// <summary>
/// Keeps the serving issuer's signing ring usable and rotated on every replica. At startup it
/// creates a missing ring or replaces one this master key cannot verify (<see cref="KeyRing.EnsureAsync"/>),
/// retrying until storage answers; <see cref="Bootstrapped"/> then completes. After that, and
/// hourly, it runs the scheduled rotation and schedules deletion of the ring secret's deprecated
/// versions. Every write is version-conditional, so replicas agree.
/// </summary>
public sealed partial class SigningKeyRotation(KeyRing ring, ISigningKeyVault vault, ILogger<SigningKeyRotation> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan BootstrapRetry = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource bootstrapped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this replica's startup bootstrap has succeeded.</summary>
    public Task Bootstrapped => this.bootstrapped.Task;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!await this.TryBootstrapAsync(stoppingToken).ConfigureAwait(false))
        {
            // A full secret refuses the bootstrap's write: free its deprecated versions before trying again.
            await this.TryPruneAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(BootstrapRetry, stoppingToken).ConfigureAwait(false);
        }

        this.bootstrapped.SetResult();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            // OCI limits versions per secret: prune first and on its own, so a full secret never blocks its own relief.
            await this.TryPruneAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                if (await ring.RotateIfDueAsync(OidcIssuer.InstanceId, stoppingToken).ConfigureAwait(false))
                {
                    this.Log(LogRotated);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
            {
                this.Log(target => LogRotationFailed(target, error));
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task TryPruneAsync(CancellationToken stoppingToken)
    {
        try
        {
            await vault.PruneAsync(OidcIssuer.InstanceId, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            this.Log(target => LogPruneFailed(target, error));
        }
    }

    private async Task<bool> TryBootstrapAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await ring.EnsureAsync(OidcIssuer.InstanceId, stoppingToken).ConfigureAwait(false))
            {
                this.Log(LogFreshRing);
            }

            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            this.Log(target => LogBootstrapFailed(target, error));
            return false;
        }
    }

    // Logging is best effort: a failing provider must not fault this service, which would stop the host.
    private void Log(Action<ILogger> write)
    {
        try
        {
            write(logger);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            _ = failure;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wrote a fresh signing key ring; tokens signed by any replaced key no longer validate")]
    private static partial void LogFreshRing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Signing key bootstrap failed; retrying")]
    private static partial void LogBootstrapFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled rotation added a signing key")]
    private static partial void LogRotated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning the signing key secret's deprecated versions failed; retrying at the next interval")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled signing key rotation failed; retrying at the next interval")]
    private static partial void LogRotationFailed(ILogger logger, Exception exception);
}
