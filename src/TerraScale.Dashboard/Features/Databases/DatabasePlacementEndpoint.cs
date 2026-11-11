using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabasePlacementEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/databases/placement");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabasePlacementEndpoints.MutateAsync(HttpContext, sessions, accounts, databases, backend));
    }
}
