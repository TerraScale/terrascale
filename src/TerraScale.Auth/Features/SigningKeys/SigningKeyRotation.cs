using TerraScale.Auth.Features.Oidc;
using ZLogger;

namespace TerraScale.Auth.Features.SigningKeys;

/// <summary>
/// Keeps the serving issuer's signing ring usable and rotated on every replica. At startup it
/// creates a missing ring or replaces one this master key cannot verify (<see cref="KeyRing.EnsureAsync"/>),
/// retrying until storage answers; <see cref="Bootstrapped"/> then completes. After that, and
/// hourly, it runs the scheduled rotation and schedules deletion of the ring secret's deprecated
/// versions. Every write is version-conditional, so replicas agree.
/// </summary>
public sealed class SigningKeyRotation(KeyRing ring, ISigningKeyVault vault, ILogger<SigningKeyRotation> logger) : BackgroundService
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
                    this.Log(target => target.ZLogInformation($"Scheduled rotation added a signing key"));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
            {
                this.Log(target => target.ZLogWarning(error, $"Scheduled signing key rotation failed; retrying at the next interval"));
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
            this.Log(target => target.ZLogWarning(error, $"Pruning the signing key secret's deprecated versions failed; retrying at the next interval"));
        }
    }

    private async Task<bool> TryBootstrapAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await ring.EnsureAsync(OidcIssuer.InstanceId, stoppingToken).ConfigureAwait(false))
            {
                this.Log(target => target.ZLogWarning($"Wrote a fresh signing key ring; tokens signed by any replaced key no longer validate"));
            }

            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
        {
            this.Log(target => target.ZLogWarning(error, $"Signing key bootstrap failed; retrying"));
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
}
