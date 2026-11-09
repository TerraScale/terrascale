using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin
{
    public sealed class AdminSectionEndpoint(DashboardSessions sessions, IPlatformRoleGateway roles) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Verbs(HttpMethods.Get, HttpMethods.Head);
            Route("/admin/{**section}");
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AdminDeny.DenyAsync(HttpContext, sessions, roles));
    }
}
