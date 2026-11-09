using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Logs
{
    public sealed class LogDrainEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases, DatabaseLogBackend backend, LogDrains drains) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/log-drain");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseLogEndpoints.MutateAsync(HttpContext, sessions, accounts, databases, backend, drains, delete: false));
    }
}
