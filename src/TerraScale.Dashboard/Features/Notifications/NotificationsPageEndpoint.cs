using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class NotificationsPageEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyExpiryNotifications expiry) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/notifications").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (!AccountRequest.TryResolvePage(http, required, out var context, out var rejection))
            {
                return rejection;
            }

            context = await NotificationCommands.RefreshAsync(accounts, context, http.RequestAborted);
            await expiry.RefreshAsync(context, http.RequestAborted);
            return NotificationView.Create(NotificationEndpoints.Page(http, context));
        }
    }
}
