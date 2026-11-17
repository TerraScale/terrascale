using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups;

public sealed class BackupsLiveEndpoint(BackupService backups) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/backups/live");

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "restore-points", async (context, database, cancellationToken) =>
        {
            var listing = await backups.ListAsync(context, database.Id, cancellationToken);
            var page = new BackupPage(ShellModel.For(HttpContext, context, database.Name + " · Backups"), database, listing);
            return new(BackupRestorePointsView.Create(page));
        }, cancellationToken);
}
