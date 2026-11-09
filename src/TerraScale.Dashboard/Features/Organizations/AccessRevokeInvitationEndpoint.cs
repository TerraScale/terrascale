using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class AccessRevokeInvitationEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, IOrganizationAccess access) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/access/invitations/{id}/revoke");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.AccessMutationAsync(HttpContext, sessions, accounts, access, placement, "revoke-invitation", HttpContext.Request.RouteValues["id"]?.ToString() ?? ""));
    }
}
