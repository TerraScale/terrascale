using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class BackupDestinationRemoveEndpoint(DashboardSessions sessions, AccountService accounts, BackupService backups) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/backups/destination-remove");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(BackupEndpoints.MutateAsync(HttpContext, sessions, accounts, backups, "destination-remove"));
    }
}
