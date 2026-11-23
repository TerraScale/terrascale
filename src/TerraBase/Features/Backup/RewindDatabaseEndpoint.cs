using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class RewindDatabaseEndpoint(BackupFlow flow) : Endpoint<BackupInput, RestoreStartResponse>
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

    public override async ValueTask<RestoreStartResponse> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.RewindAsync(HttpContext.Caller, request, cancellationToken);
    }
}
