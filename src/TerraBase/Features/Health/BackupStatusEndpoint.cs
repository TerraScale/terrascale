using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

internal sealed class BackupStatusEndpoint(TerraBaseOptions options, IBackupContinuity continuity)
    : EndpointWithoutRequest<BackupHealthResponse>
{
    public override void Configure()
    {
        Get("/v1/backup");
        Name("backupStatus");
        Summary("Node continuous backup status");
        Description("The archive position and restore window of this node's system database, from the continuity service.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<BackupHealthResponse> HandleAsync(CancellationToken cancellationToken) =>
        await BackupProbe.ProbeAsync(continuity, options, cancellationToken);
}
