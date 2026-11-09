using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class CreateAlertEndpoint(DashboardSessions sessions, AccountService accounts, AlertRules rules) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/notifications/alerts");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AlertEndpoints.MutateAsync(HttpContext, sessions, accounts, rules, ""));
    }
}
