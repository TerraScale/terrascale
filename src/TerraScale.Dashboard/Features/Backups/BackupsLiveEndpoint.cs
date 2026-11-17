using TerraScale.Dashboard.Common.Live;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups;

public sealed class BackupsLiveEndpoint(BackupService backups) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/backups/live");

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "restore-points", async (context, database, cancellationToken) =>
        {
            var listing = await backups.ListAsync(context, database.Id, cancellationToken);
            var page = BackupListState.Read(HttpContext.Request).Page(HttpContext, context, database, listing);
            return new(BackupRestorePointsView.Create(page));
        }, cancellationToken);
}
