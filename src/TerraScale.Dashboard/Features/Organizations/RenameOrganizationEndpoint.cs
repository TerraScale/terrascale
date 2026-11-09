using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class RenameOrganizationEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/rename");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.MutateAsync(HttpContext, sessions, accounts, placement, "rename"));
    }
}
