using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class PlanPointInTimeEndpoint(BackupFlow flow) : Endpoint<BackupInput, RestoreWindowResponse>
{
    public override void Configure()
    {
        Post(BackupFlow.Database + "/point-in-time/plan");
        Name("planPointInTime");
        Summary("Checks whether a point in time is restorable");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RestoreWindowResponse> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.WindowAsync(HttpContext.Caller, request, cancellationToken);
    }
}
