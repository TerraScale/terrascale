using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class GetBackupPolicyEndpoint(BackupFlow flow) : Endpoint<BackupInput, BackupPolicyResponse>
{
    public override void Configure()
    {
        Get(BackupFlow.Database + "/backup-policy");
        Name("getBackupPolicy");
        Summary("Reads the database's backup retention");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<BackupPolicyResponse> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.PolicyAsync(HttpContext.Caller, request, write: false, cancellationToken);
    }
}
