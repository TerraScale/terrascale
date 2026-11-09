using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class RestoreBackupEndpoint(DashboardSessions sessions, AccountService accounts, BackupService backups) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/backups/restore");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(BackupEndpoints.MutateAsync(HttpContext, sessions, accounts, backups, "restore"));
    }
}
