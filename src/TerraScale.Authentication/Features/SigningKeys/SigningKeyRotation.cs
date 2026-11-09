using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.SigningKeys;

public sealed class SigningKeyRotation(AuthSchema schema, ClientRegistration clients, PlatformBootstrap platform, KeyRing ring, ILogger<SigningKeyRotation> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstBootstrapRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBootstrapRetry = TimeSpan.FromMinutes(5);

    private readonly TaskCompletionSource bootstrapped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this replica's startup bootstrap has succeeded.</summary>
    public Task Bootstrapped => this.bootstrapped.Task;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retry = FirstBootstrapRetry;
        while (!await this.TryBootstrapAsync(stoppingToken).ConfigureAwait(false))
        {
            await Task.Delay(retry, stoppingToken).ConfigureAwait(false);
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxBootstrapRetry.Ticks));
        }

        this.bootstrapped.SetResult();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (await ring.RotateIfDueAsync(OidcIssuer.InstanceId, stoppingToken).ConfigureAwait(false))
                {
                    this.Log(target => target.ZLogInformation($"Scheduled rotation added a signing key"));
                }
            }
            // A failure while stopping is not reported: the timer wait below ends the loop. Rethrowing it would fault
            // the host's StopAsync.
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    this.Log(target => target.ZLogWarning(error, $"Scheduled signing key rotation failed; retrying at the next interval"));
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task<bool> TryBootstrapAsync(CancellationToken stoppingToken)
    {
        try
        {
            await schema.MigrateAsync(stoppingToken).ConfigureAwait(false);
            await platform.EnsureAsync(stoppingToken).ConfigureAwait(false);
            await clients.RegisterAllAsync(stoppingToken).ConfigureAwait(false);
            if (await ring.ProvisionAsync(OidcIssuer.InstanceId, stoppingToken).ConfigureAwait(false))
            {
                this.Log(target => target.ZLogInformation($"Audit: startup provisioned the platform issuer's first signing key"));
            }
            return true;
        }
        // As above: while stopping, the retry delay ends the loop.
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                this.Log(target => target.ZLogWarning(error, $"Storage and signing key bootstrap failed; retrying"));
            }

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
