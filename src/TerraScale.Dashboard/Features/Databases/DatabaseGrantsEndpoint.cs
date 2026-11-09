using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabaseGrantsEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases, IDatabaseBackend backend) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/databases/grants");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseGrants.MutateAsync(HttpContext, sessions, accounts, databases, backend, "grants"));
    }
}
