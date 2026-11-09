using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Logs
{
    public sealed class DatabaseLogsPageEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases, DatabaseLogBackend backend, LogDrains drains) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/logs");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseLogEndpoints.PageAsync(HttpContext, sessions, accounts, databases, backend, drains));
    }
}
