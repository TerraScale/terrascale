using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed class NotificationsLiveEndpoint(DashboardSessions sessions, AccountService accounts, LiveView live, ApiKeyExpiryNotifications expiry) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/notifications/live").AuthenticateInHandler();

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var http = HttpContext;
        var view = http.Request.Query["view"].ToString();
        var errorRegion = view switch { "inbox" => "notification-inbox", "dialog" => "ts-notifications-body", _ => null };
        if (errorRegion is null && view != "shell")
        {
            return TypedResults.BadRequest();
        }

        return await live.StreamAsync(http, token => LiveView.ReadWithAccountAsync(http, sessions, accounts, errorRegion,
            async (context, cancellationToken) =>
            {
                context = await NotificationCommands.RefreshAsync(accounts, context, cancellationToken);
                await expiry.RefreshAsync(context, cancellationToken);
                var page = NotificationEndpoints.Page(http, context, view == "dialog");
                return new(view == "shell" ? NotificationShellPatch.Create(page.Shell) : NotificationRegion.Create(page));
            }, token), live.ChangesAsync(cancellationToken), cancellationToken);
    }
}
