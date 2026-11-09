using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DeleteDatabaseEndpoint(DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/databases/delete");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseEndpoints.OperateAsync(HttpContext, sessions, accounts, databases, backend, "delete"));
    }
}
