using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    public sealed class AuthPageEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/auth");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthenticationConsole.RootAsync(HttpContext, sessions, accounts, projects, "/auth"));
    }
}
