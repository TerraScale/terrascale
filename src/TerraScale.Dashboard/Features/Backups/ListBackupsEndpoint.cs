using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;
namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class ListBackupsEndpoint(DashboardSessions sessions, AccountService accounts, BackupService backups) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/backups");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var (limit, after) = CursorPaging.Read(http.Request);
                var id = http.Request.Query["databaseId"].ToString();
                var before = http.Request.Query["before"].ToString();
                if (http.Request.IsDatastarRequest)
                {
                    var listing = await backups.ListAsync(context, id, http.RequestAborted);
                    var database = DatabaseService.Database(context, id);
                    var boundary = -1;
                    for (var index = 0; index < listing.Backups.Count; index++)
                    {
                        if (listing.Backups[index].Id == before)
                        {
                            boundary = index;
                            break;
                        }
                    }

                    IReadOnlyList<BackupView> page = boundary < 0 ? [] : [.. listing.Backups.Skip(boundary + 1).Take(RestorePointsModel.PageSize)];
                    var next = boundary >= 0 && listing.Backups.Count > boundary + 1 + page.Count
                        ? listing.Backups[boundary + page.Count].Id : null;
                    http.Response.Headers["datastar-selector"] = "#restore-points-more";
                    http.Response.Headers["datastar-mode"] = "outer";
                    await DatastarEvents.PatchElementsAsync(http, RestorePointsView.Create(
                        new(ShellModel.For(http, context, database.Name + " · Backups"), database, "", "", "", page, next)));
                    return Results.Empty;
                }

                if (before.Length > 0)
                {
                    return Results.BadRequest("Use the restore points list to page.");
                }

                var slice = await backups.ListPageAsync(context, id, after.Length == 0 ? null : after, limit, http.RequestAborted);
                return Results.Json(new BackupRestList(slice.Policy, slice.Items, slice.NextCursor, slice.HasMore, limit));
            }
            catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
                (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { return BackupEndpoints.FailureResult(error); }
        }
    }
}
