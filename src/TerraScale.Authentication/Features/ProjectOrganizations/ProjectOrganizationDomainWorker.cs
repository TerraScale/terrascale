using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDomainWorker(AuthSchema schema, ProjectOrganizationDirectory organizations,
    TimeProvider clock, ILogger<ProjectOrganizationDomainWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await organizations.RecheckDomainsAsync(stoppingToken);
            }
            catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
            {
                RecheckFailed(logger, failure);
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Organization domain rechecks failed.")]
    private static partial void RecheckFailed(ILogger logger, Exception exception);
}
