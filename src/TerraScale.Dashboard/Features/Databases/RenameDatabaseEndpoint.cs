using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class RenameDatabaseEndpoint(DashboardSessions sessions, AccountService accounts, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/databases/rename");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(DatabaseSettingsEndpoints.RenameAsync(HttpContext, sessions, accounts, databases));
    }
}
