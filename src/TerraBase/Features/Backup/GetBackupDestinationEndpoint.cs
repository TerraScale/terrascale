using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class GetBackupDestinationEndpoint(BackupDestinationStore destinations) : EndpointWithoutRequest<GetBackupDestinationResult>
{
    public override void Configure()
    {
        Get(BackupDestinationStore.Route);
        Name("getBackupDestination");
        Summary("Reads the project's customer backup destination");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override ValueTask<GetBackupDestinationResult> HandleAsync(CancellationToken cancellationToken) =>
        destinations.GetAsync(HttpContext.Caller, HttpContext.Request.RouteValues["project"] as string, cancellationToken);
}
