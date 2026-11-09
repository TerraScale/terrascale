using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class DatabasesPageEndpoint(DashboardSessions sessions, AccountService accounts, IDatabaseBackend backend, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/databases");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseEndpoints.ListAsync(HttpContext, sessions, accounts, backend, databases,
                HttpContext.RequestServices.GetRequiredService<TerraScale.Dashboard.Features.Backups.BackupService>()));
    }
}
