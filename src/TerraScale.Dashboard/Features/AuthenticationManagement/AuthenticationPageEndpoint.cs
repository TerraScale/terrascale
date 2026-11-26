using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    public sealed class AuthenticationPageEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/authentication").AuthenticateInHandler();

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthenticationConsole.RootAsync(HttpContext, sessions, accounts, projects, "/authentication"));
    }
}
