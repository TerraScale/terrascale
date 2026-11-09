using TerraScale.Dashboard.Features.Billing;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Organizations.Wizard
{
    public sealed class NewOrganizationEndpoint(DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, StripeBilling payments) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/organization/new-organization");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationWizard.MutateAsync(HttpContext, sessions, accounts, placement, payments));
    }
}
