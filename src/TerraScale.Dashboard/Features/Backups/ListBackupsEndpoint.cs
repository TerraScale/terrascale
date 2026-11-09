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
                var slice = await backups.ListPageAsync(context, id, after.Length == 0 ? null : after, limit, http.RequestAborted);
                return Results.Json(new BackupRestList(slice.Policy, slice.Items, slice.NextCursor, slice.HasMore, limit));
            }
            catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
                (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { return BackupEndpoints.FailureResult(error); }
        }
    }
}
