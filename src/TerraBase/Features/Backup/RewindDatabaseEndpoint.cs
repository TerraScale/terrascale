using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class RewindDatabaseEndpoint(BackupFlow flow) : Endpoint<BackupInput, Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Post(BackupFlow.Database + "/rewind");
        Name("rewindDatabase");
        Summary("Rewinds the database in place to a point in time or bookmark");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<Accepted<RestoreOperationBody>, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.RewindAsync(HttpContext.Caller, request, cancellationToken);
    }
}
