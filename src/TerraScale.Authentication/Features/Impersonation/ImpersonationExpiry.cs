using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed class ImpersonationExpiry(AuthSchema schema, Impersonations impersonations, TimeProvider clock, ILogger<ImpersonationExpiry> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await impersonations.ExpireAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Impersonation expiry could not persist its audit events.");
            }
        }
    }
}
