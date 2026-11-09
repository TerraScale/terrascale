using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin
{
    public sealed class AdminPlanDeleteEndpoint(DashboardSessions sessions, IPlatformRoleGateway roles) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/admin/plans/{planID}/delete");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AdminDeny.DenyAsync(HttpContext, sessions, roles));
    }
}
