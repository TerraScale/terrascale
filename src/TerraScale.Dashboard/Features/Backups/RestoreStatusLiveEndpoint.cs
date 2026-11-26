using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups;

public sealed class RestoreStatusLiveEndpoint(BackupService backups) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/databases/{databaseId}/backups/restore/live").AuthenticateInHandler();

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await LiveDatabaseView.StreamAsync(HttpContext, "restore-status", async (context, database, cancellationToken) =>
        {
            var restore = await backups.RestoreStatusAsync(context, database.Id, HttpContext.Request.Query["restore"].ToString(), cancellationToken);
            var page = new BackupPage(ShellModel.For(HttpContext, context, database.Name + " · Backups"), database, null) { Restore = restore };
            return new(RestoreStatusView.Create(page), restore.Status != "running");
        }, cancellationToken);
}
