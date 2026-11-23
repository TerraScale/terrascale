using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

internal sealed class SetBackupPolicyEndpoint(BackupFlow flow) : Endpoint<BackupInput, Result<BackupPolicyBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
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

    public override async ValueTask<Result<BackupPolicyBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(BackupInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await flow.PolicyAsync(HttpContext.Caller, request, write: true, cancellationToken);
    }
}
