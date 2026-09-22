using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications.Endpoints;

/// <summary>POST /api/v1/notifications/{id}/read — marks one notification read
/// and returns to the inbox (parity with NotificationMutationButton mode=one).</summary>
public sealed class MarkNotificationReadEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/notifications/{notificationId}/read");
        Summary("Marks one notification as read");
        Tags("Notifications");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var id = HttpContext.Request.RouteValues["notificationId"] as string ?? "";
        store.MarkNotificationRead(id);
        return Task.FromResult<IResult>(Results.Redirect("/notifications", false, false));
    }
}
