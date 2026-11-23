using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class GetRestoreOperationEndpoint(BackupFlow flow) : Endpoint<BackupInput, RestoreOperationResponse>
{
    public override void Configure()
    {
        Get(BackupFlow.Database + "/restores/{operation}");
        Name("getRestoreOperation");
        Summary("Reads a restore, rewind or branch operation");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RestoreOperationResponse> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.OperationAsync(HttpContext.Caller, request, cancellationToken);
    }
}
