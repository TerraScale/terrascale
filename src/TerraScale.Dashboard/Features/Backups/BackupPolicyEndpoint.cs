using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed class BackupPolicyEndpoint(DashboardSessions sessions, AccountService accounts, BackupService backups) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/backups/policy");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(BackupEndpoints.MutateAsync(HttpContext, sessions, accounts, backups, "policy"));
    }
}
