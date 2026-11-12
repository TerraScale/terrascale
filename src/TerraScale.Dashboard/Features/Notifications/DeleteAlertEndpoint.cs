using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class DeleteAlertEndpoint(DashboardSessions sessions, AccountService accounts, AlertRules rules, AuthFlows flows) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/notifications/alerts/{id}/delete");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AlertEndpoints.MutateAsync(HttpContext, sessions, accounts, rules, flows, "delete"));
    }
}
