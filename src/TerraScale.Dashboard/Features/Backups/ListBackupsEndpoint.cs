using TerraScale.Dashboard.Platform.Datastar;
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
                return TypedResults.Unauthorized();
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

                    var count = Math.Min(listing.Backups.Count, Math.Max(0, boundary + 1) + RestorePointsModel.PageSize);
                    var state = BackupListState.Read(http.Request) with
                    { Through = count > 0 ? listing.Backups[count - 1].Id : null };
                    await DatastarEvents.PatchElementsAsync(http, BackupRestorePointsView.Create(state.Page(http, context, database, listing)));
                    return TypedResults.Empty;
                }

                if (before.Length > 0)
                {
                    return TypedResults.BadRequest("Use the restore points list to page.");
                }

                var slice = await backups.ListPageAsync(context, id, after.Length == 0 ? null : after, limit, http.RequestAborted);
                return TypedResults.Json(new BackupRestList(slice.Policy, slice.Items, slice.NextCursor, slice.HasMore, limit));
            }
            catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
                (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { return BackupEndpoints.FailureResult(error); }
        }
    }
}
