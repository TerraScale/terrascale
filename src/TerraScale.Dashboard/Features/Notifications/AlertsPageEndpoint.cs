using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class AlertsPageEndpoint(DashboardSessions sessions, AccountService accounts, AlertRules rules, AuthFlows flows) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/notifications/alerts");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AlertEndpoints.PageAsync(HttpContext, sessions, accounts, rules, flows));
    }
}
