using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class CancelOwnershipTransferEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, IOrganizationAccess access) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/members/ownership/cancel");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.AccessMutationAsync(HttpContext, sessions, accounts, access, placement, "ownership/cancel"));
    }
}
