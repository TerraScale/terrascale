using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed class AccessMemberRoleEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, IOrganizationAccess access) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/access/members/{id}/role");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationEndpoints.AccessMutationAsync(HttpContext, sessions, accounts, access, placement, "role", HttpContext.Request.RouteValues["id"]?.ToString() ?? ""));
    }
}
