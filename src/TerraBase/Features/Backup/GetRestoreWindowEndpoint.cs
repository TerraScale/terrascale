using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class GetRestoreWindowEndpoint(BackupFlow flow) : Endpoint<BackupInput, Result<RestoreWindowBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Get(BackupFlow.Database + "/point-in-time");
        Name("getRestoreWindow");
        Summary("The earliest and latest restorable points");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<RestoreWindowBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.WindowAsync(HttpContext.Caller, request, cancellationToken);
    }
}
