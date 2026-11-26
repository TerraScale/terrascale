using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class NotificationDialogEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyExpiryNotifications expiry) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/notifications/dialog").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            context = await NotificationCommands.RefreshAsync(accounts, context, http.RequestAborted);
            if (!http.Request.IsDatastarRequest)
            {
                return TypedResults.Redirect("/notifications");
            }

            await expiry.RefreshAsync(context, http.RequestAborted);
            await DatastarEvents.PatchElementsAsync(http, NotificationPatch.Create(NotificationEndpoints.Page(http, context, true)));
            return TypedResults.Empty;
        }
    }
}
