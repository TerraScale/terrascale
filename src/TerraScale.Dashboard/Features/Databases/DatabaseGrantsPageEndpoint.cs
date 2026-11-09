using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabaseGrantsPageEndpoint(DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases/{databaseId}/grants");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseEndpoints.TabAsync(HttpContext, sessions, accounts, backend, databases, "grants"));
    }
}
