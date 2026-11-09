using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class InviteMemberEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, IOrganizationAccess access) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/members/invite");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.AccessMutationAsync(HttpContext, sessions, accounts, access, placement, "invite"));
    }
}
