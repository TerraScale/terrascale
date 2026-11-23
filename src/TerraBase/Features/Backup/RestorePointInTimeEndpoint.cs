using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class RestorePointInTimeEndpoint(BackupFlow flow) : Endpoint<BackupInput, RestoreStartResponse>
{
    public override void Configure()
    {
        Post(BackupFlow.Database + "/point-in-time/restore");
        Name("restoreDatabase");
        Summary("Restores the database at a point in time or bookmark into a new database");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RestoreStartResponse> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.RestoreAsync(HttpContext.Caller, request, BackupFlow.Target(request), cancellationToken);
    }
}
