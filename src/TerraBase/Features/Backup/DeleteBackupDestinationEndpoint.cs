using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class DeleteBackupDestinationEndpoint(BackupDestinationStore destinations) : EndpointWithoutRequest<DeleteBackupDestinationResult>
{
    public override void Configure()
    {
        Delete(BackupDestinationStore.Route);
        Name("deleteBackupDestination");
        Summary("Removes the project's customer backup destination");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override ValueTask<DeleteBackupDestinationResult> HandleAsync(CancellationToken cancellationToken) =>
        destinations.DeleteAsync(HttpContext.Caller, HttpContext.Request.RouteValues["project"] as string, cancellationToken);
}
