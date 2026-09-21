using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Notifications.Pages.Notifications;

public sealed class NotificationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<NotificationsSlice, NotificationsPageModel>(store, "/notifications")
{
    protected override NotificationsPageModel Load() => NotificationsPageModel.From(Store, PageRoute);
}
