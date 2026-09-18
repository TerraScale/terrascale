using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Notifications;

public sealed record NotificationsPageModel : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class NotificationsPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<NotificationsSlice, NotificationsPageModel>(store, "/notifications")
{
    protected override NotificationsPageModel Load() => new() { Shell = ShellFactory.Create(Store, PageRoute) };
}
