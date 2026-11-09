using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    public sealed class AuthManagementPostEndpoint(DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/auth-management/{**path}");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AuthenticationConsole.PostAsync(HttpContext, HttpContext.Request.RouteValues["path"]?.ToString() ?? "", sessions, accounts, projects));
    }
}
