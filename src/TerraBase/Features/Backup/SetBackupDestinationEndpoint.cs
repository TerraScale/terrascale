using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class SetBackupDestinationEndpoint(BackupDestinationStore destinations) : Endpoint<BackupDestinationInput, SetBackupDestinationResult>
{
    public override void Configure()
    {
        Put(BackupDestinationStore.Route);
        Name("setBackupDestination");
        Summary("Verifies and sets the project's customer backup destination");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override ValueTask<SetBackupDestinationResult> HandleAsync(BackupDestinationInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return destinations.SetAsync(HttpContext.Caller, HttpContext.Request.RouteValues["project"] as string, request, cancellationToken);
    }
}
