using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Admin
{
    public sealed class AdminPlanVisibilityEndpoint(DashboardSessions sessions, IPlatformRoleGateway roles) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/admin/plans/{planID}/visibility");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AdminDeny.DenyAsync(HttpContext, sessions, roles));
    }
}
