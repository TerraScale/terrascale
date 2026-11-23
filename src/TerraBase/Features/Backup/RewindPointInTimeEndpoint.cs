using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class RewindPointInTimeEndpoint(BackupFlow flow) : Endpoint<BackupInput, RestoreStartResponse>
{
    public override void Configure()
    {
        Post(BackupFlow.Database + "/point-in-time/rewind");
        Name("rewindPointInTime");
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
