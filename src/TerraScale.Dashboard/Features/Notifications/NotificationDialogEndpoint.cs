using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications
{
    public sealed class NotificationDialogEndpoint(DashboardSessions sessions, AccountService accounts) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Get("/notifications/dialog");

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            context = await NotificationCommands.RefreshAsync(accounts, context, http.RequestAborted);
            if (http.Request.Headers["Datastar-Request"] != "true")
            {
                return Results.Redirect("/notifications");
            }

            await ApiKeyExpiryNotifications.RefreshAsync(http, context);
            await DatastarEvents.PatchElementsAsync(http, NotificationPatch.Create(NotificationEndpoints.Page(http, context, true)));
            return Results.Empty;
        }
    }
}
