using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class LeaveOrganizationEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, OrganizationLifecycleService lifecycle) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/leave");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.LifecycleAsync(HttpContext, sessions, accounts, lifecycle, placement, "leave"));
    }
}
