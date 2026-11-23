using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class GetBackupStatusEndpoint(BackupFlow flow) : Endpoint<BackupInput, Result<BackupStatusBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Get(BackupFlow.Database + "/backups");
        Name("getBackupStatus");
        Summary("Continuous backup state and the restorable window");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<BackupStatusBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.StatusAsync(HttpContext.Caller, request, archiveNow: false, cancellationToken);
    }
}
