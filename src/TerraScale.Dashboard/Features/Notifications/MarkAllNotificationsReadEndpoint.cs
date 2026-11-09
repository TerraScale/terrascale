using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class MarkAllNotificationsReadEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/notifications/read-all");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(NotificationEndpoints.MarkAsync(HttpContext, sessions, accounts, null));
    }
}
