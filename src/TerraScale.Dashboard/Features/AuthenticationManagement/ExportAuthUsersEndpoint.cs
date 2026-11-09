using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    public sealed class ExportAuthUsersEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/auth-management/users/export");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthenticationConsole.ExportUsersAsync(HttpContext, sessions, accounts, projects));
    }
}
