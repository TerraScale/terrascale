using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class CreateDatabaseEndpoint(DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/databases");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseEndpoints.CreateAsync(HttpContext, sessions, accounts, databases, backend));
    }
}
