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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retry = FirstBootstrapRetry;
        while (!await this.TryBootstrapAsync(stoppingToken))
        {
            schema.MarkBootstrapFailed();
            await Task.Delay(retry, stoppingToken);
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxBootstrapRetry.Ticks));
        }

        schema.MarkSeeded();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var rotated = await ring.RotateIfDueAsync(OidcIssuer.InstanceId, stoppingToken);
                this.Record(rotated);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    this.Log(target => target.ZLogWarning(error, $"Scheduled signing key rotation failed; retrying at the next interval"));
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private bool Record(RotateIfDueResult rotated) => rotated switch
    {
        Added => this.RecordAdded(),
        AuthConflict full => this.RecordConflict(full),
        AuthCompleted => false,
    };

    private bool RecordAdded()
    {
        this.Log(target => target.ZLogInformation($"Scheduled rotation added a signing key"));
        return true;
    }

    private bool RecordConflict(AuthConflict full)
    {
        this.Log(target => target.ZLogWarning($"Scheduled signing key rotation failed: {full.Message:@Reason}; retrying at the next interval"));
        return true;
    }

    private async Task<bool> TryBootstrapAsync(CancellationToken stoppingToken)
    {
        var step = "schema migration";
        try
        {
            await schema.MigrateAsync(stoppingToken);
            step = "platform seed";
            await platform.EnsureAsync(stoppingToken);
            step = "client registration";
            await clients.RegisterAllAsync(stoppingToken);
            step = "signing key provisioning";
            if (await ring.ProvisionAsync(OidcIssuer.InstanceId, stoppingToken))
            {
                this.Log(target => target.ZLogInformation($"Audit: startup provisioned the platform issuer's first signing key"));
            }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                this.Log(target => target.ZLogWarning(error, $"Startup bootstrap failed at {step:@Step}; retrying"));
            }

            return false;
        }
    }

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
