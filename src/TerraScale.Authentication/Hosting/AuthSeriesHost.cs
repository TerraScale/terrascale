using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Hosting;

internal sealed class AuthSeriesHost : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        AuthMetrics.Publish();
        EmailAdmission.Publish();
        DeletionCleanup.Publish();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
