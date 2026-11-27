using System.Text.Json;
using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed class NotificationsLiveEndpoint(DashboardSessions sessions, AccountService accounts, LiveView live, ApiKeyExpiryNotifications expiry) : EndpointWithoutRequest<IResult>
{
    public override void Configure() => Get("/notifications/live").AuthenticateInHandler();

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var http = HttpContext;
        var view = http.Request.Query["view"].ToString();
        var region = view switch { "shell" => "ts-notifications-topbar", "inbox" => "notification-inbox", "dialog" => "ts-notifications-body", _ => "" };
        if (region.Length == 0)
        {
            return TypedResults.BadRequest();
        }

        async Task<LiveFragment> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                var required = await AccountRequest.RequireAsync(http, sessions, accounts);
                var (context, error) = required switch
                {
                    AccountContext active => (active, (LiveFragment?)null),
                    AccountUnauthenticated => (null, new LiveFragment(LiveErrorView.Create(new(region, 401, "Your session may have expired. Sign in to TerraScale and try again.")), true)),
                    SessionUnavailable => (null, new LiveFragment(LiveErrorView.Create(new(region, 503, "A TerraScale service is temporarily unavailable. Try again shortly.")), true)),
                };
                if (error is not null || context is null)
                {
                    return error ?? new(LiveErrorView.Create(new(region, 503, "A TerraScale service is temporarily unavailable. Try again shortly.")), true);
                }

                context = await NotificationCommands.RefreshAsync(accounts, context, cancellationToken);
                await expiry.RefreshAsync(context, cancellationToken);
                var page = NotificationEndpoints.Page(http, context, view == "dialog");
                return new(view == "shell" ? NotificationShellPatch.Create(page.Shell) : NotificationRegion.Create(page));
            }
            catch (OrganizationMfaRequiredException error)
            {
                return new(LiveErrorView.Create(new(region, 403, error.Message)), true);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or JsonException or OperationCanceledException))
            {
                return new(LiveErrorView.Create(new(region, DatabaseResults.Status(error), DatabaseResults.Message(error))), true);
            }
        }

        return await live.StreamAsync(http, ReadAsync, live.ChangesAsync(cancellationToken), cancellationToken);
    }
}
