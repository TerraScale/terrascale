using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class MarkNotificationReadEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/notifications/{id}/read");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(NotificationEndpoints.MarkAsync(HttpContext, sessions, accounts, HttpContext.Request.RouteValues["id"]?.ToString() ?? ""));
    }
}
