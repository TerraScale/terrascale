using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Sso
{
    public sealed class OrganizationSsoDeleteEndpoint(DashboardSessions sessions, AccountService accounts, OrganizationSsoService sso, IWorkspacePlacement placement) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/sso/delete");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationSsoEndpoints.MutateAsync(HttpContext, sessions, accounts, sso, placement, "/delete"));
    }
}
