using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class BackupRestoreStatusEndpoint(DashboardSessions sessions, AccountService accounts, BackupService backups) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/api/v1/dashboard/backups/restore").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolveApi(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            try
            {
                var status = await backups.RestoreStatusAsync(context, http.Request.Query["databaseId"].ToString(), http.Request.Query["operation"].ToString(), http.RequestAborted);
                return TypedResults.Json(new RestoreStatusResponse(status.OperationId, status.Status, status.TargetDatabaseId, status.TargetName, status.ErrorCode, status.ErrorMessage));
            }
            catch (Exception error) when (BackupEndpoints.KnownFailure(error) &&
                (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
            { return BackupEndpoints.FailureResult(error); }
        }
    }
}
