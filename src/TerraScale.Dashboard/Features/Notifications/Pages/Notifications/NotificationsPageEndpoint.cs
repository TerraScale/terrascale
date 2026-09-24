using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications.Pages.Notifications;

public sealed class NotificationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<NotificationsSlice, NotificationsPageModel>(store, "/notifications")
{
    protected override NotificationsPageModel Load()
    {
        var query = HttpContext.Request.Query;
        var page = int.TryParse(query["page"].FirstOrDefault(), out var parsedPage) ? parsedPage : 1;
        return NotificationsPageModel.From(Store, PageRoute, query["status"].FirstOrDefault(), page);
    }
}
