using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

internal readonly union BackupStatusResponse(BackupBody, UnavailableStatus<BackupBody>, CallerForbidden);

internal sealed class BackupStatusEndpoint(TerraBaseOptions options, IBackupContinuity continuity)
    : EndpointWithoutRequest<BackupStatusResponse>
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

    public override async ValueTask<BackupStatusResponse> HandleAsync(CancellationToken cancellationToken)
    {
        if (!CallerAuthenticationExtensions.OperatesNode(HttpContext.Caller))
        {
            return new CallerForbidden("backup status needs the peer credential or an operator admin grant", "forbidden_caller");
        }

        var probe = await BackupProbe.ProbeAsync(continuity, options, cancellationToken);
        return probe switch
        {
            BackupBody body => body,
            UnavailableStatus<BackupBody> unavailable => unavailable,
        };
    }
}
