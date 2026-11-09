using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Analytics
{
    public sealed class AnalyticsPageEndpoint(DashboardSessions sessions, AccountService accounts, AnalyticsService analytics) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/analytics");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(AnalyticsEndpoints.PageAsync(HttpContext, sessions, accounts, analytics));
    }
}
