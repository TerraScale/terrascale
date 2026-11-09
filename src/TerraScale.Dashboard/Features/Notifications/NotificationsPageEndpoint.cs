using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class NotificationsPageEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/notifications");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            context = await NotificationCommands.RefreshAsync(accounts, context, http.RequestAborted);
            await ApiKeyExpiryNotifications.RefreshAsync(http, context);
            return NotificationView.Create(NotificationEndpoints.Page(http, context));
        }
    }
}
