using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed class OrganizationChooseEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/organizations/choose");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(OrganizationPicker.PageAsync(HttpContext, sessions, accounts));
    }
}
