using TerraBase.Features.Callers;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class RestoreBackupEndpoint(BackupFlow flow) : Endpoint<BackupInput, Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Post(BackupFlow.Database + "/backups/{backup}/restore");
        Name("restoreBackup");
        Summary("Restores the database as of a backup into a new database");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.RestoreAsync(HttpContext.Caller, request, ManagementService.Id(request.Backup) is { } backup ? new TargetChoice(backup.Time) : new TargetChoice(new Validation("backup must be a backup ULID", "invalid_backup")), cancellationToken);
    }
}
