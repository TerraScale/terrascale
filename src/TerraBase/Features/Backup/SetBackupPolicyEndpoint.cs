using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class SetBackupPolicyEndpoint(BackupFlow flow) : Endpoint<BackupInput, BackupPolicyResponse>
{
    public override void Configure()
    {
        Put(BackupFlow.Database + "/backup-policy");
        Name("setBackupPolicy");
        Summary("Sets the database's backup retention in days");
        Tags("Backups");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<BackupPolicyResponse> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.PolicyAsync(HttpContext.Caller, request, write: true, cancellationToken);
    }
}
