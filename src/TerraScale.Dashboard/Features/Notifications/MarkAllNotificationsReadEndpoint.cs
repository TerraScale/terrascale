using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications;

/// <summary>POST /api/v1/notifications/read-all — marks every notification
/// read (parity with NotificationMutationButton mode=all).</summary>
public sealed class MarkAllNotificationsReadEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/notifications/read-all");
        Summary("Marks all notifications as read");
        Tags("Notifications");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        store.MarkAllNotificationsRead();
        return Task.FromResult<IResult>(Results.Redirect("/notifications", false, false));
    }
}
