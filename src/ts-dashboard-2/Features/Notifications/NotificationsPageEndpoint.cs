using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Notifications;

public sealed class NotificationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<NotificationsSlice, NotificationsPageModel>(store, "/notifications")
{
    protected override NotificationsPageModel Load() => NotificationsPageModel.From(Store, PageRoute);
}
