using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraScale.Contracts.Continuity;
using TerraScale.Endpoints;

namespace TerraBase.Features.Health;

internal sealed class BackupzEndpoint(TerraBaseOptions options, IBackupContinuity continuity)
    : EndpointWithoutRequest<Result<BackupBody, UnavailableStatus<BackupBody>>>
{
    public override void Configure()
    {
        Get("/backupz");
        Name("backupz");
        Summary("Continuous backup probe");
        Description("200 while continuous backup of this node's system database is healthy (pending before its first archive); 503 when backup is disabled or unhealthy.");
        Tags("Health");
        AllowAnonymous();
        Metadata(ListenerMetadata.Probe);
    }

    public override async ValueTask<Result<BackupBody, UnavailableStatus<BackupBody>>> HandleAsync(CancellationToken cancellationToken) =>
        await BackupProbe.ProbeAsync(continuity, options, cancellationToken);
}

internal static class BackupProbe
{
    public static async ValueTask<Result<BackupBody, UnavailableStatus<BackupBody>>> ProbeAsync(
        IBackupContinuity continuity, TerraBaseOptions options, CancellationToken cancellationToken) =>
        await continuity.GetStatusAsync(SystemDatabase.Id, cancellationToken) switch
        {
            BackupStatus status => new BackupBody("ok", options.Region, null, null, status.ArchivedThrough.Encode(), status.EarliestRestorePointUtc,
                status.LatestRestorePointUtc, status.LastSnapshotAtUtc, status.ArchivedBytes, status.PendingSegments),
            NotFound => new BackupBody("pending", options.Region, null, "no archive of the system database exists yet", null, null, null, null, null, null),
            Unavailable error => new UnavailableStatus<BackupBody>(
                new BackupBody("unavailable", options.Region, error.Code, error.Message, null, null, null, null, null, null), HealthJsonContext.Default),
        };
}
