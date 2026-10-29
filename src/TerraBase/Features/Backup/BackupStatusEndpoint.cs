using TerraScale.Endpoints;

namespace TerraBase.Features.Backup;

/// <summary><c>GET /v1/backup</c> on the internal listener: what this node ships and how far each file is.</summary>
public sealed class BackupStatusEndpoint(IContinuousBackup backup) : EndpointWithoutRequest<BackupStatus>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/backup");
        Name("backupStatus");
        Summary("Continuous backup status of this node");
        Description("Every storage file this node ships to the backup bucket: its generation, frames read, when its newest data reached the bucket, the lag of data still waiting, and the kind of its last failure. Files are named by their bucket directory (organization, project and database ids, followed by nodes and the stable node identity; system files use _system/nodes and the stable node identity).");
        Tags("Administration");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<BackupStatus> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(backup.Status);
    }
}
