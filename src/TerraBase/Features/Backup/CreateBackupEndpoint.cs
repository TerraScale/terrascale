using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class CreateBackupEndpoint(BackupFlow flow) : Endpoint<BackupInput, Result<BackupStatusBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Post(BackupFlow.Database + "/backups");
        Name("createBackup");
        Summary("Archives the database's pending history now");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<BackupStatusBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.StatusAsync(HttpContext.Caller, request, archiveNow: true, cancellationToken);
    }
}
