using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Notifications.Pages.Notifications;

public sealed record NotificationsPageModel(
    int Unread,
    int Critical,
    int Total,
    IReadOnlyList<Notification> Notifications) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static NotificationsPageModel From(DashboardStore store, string href) => new(
        Unread: store.UnreadNotifications,
        Critical: store.Notifications.Count(n => n.Status == "Unread" && n.Severity == "danger"),
        Total: store.Notifications.Length,
        Notifications: [.. store.Notifications])
    {
        Shell = ShellFactory.Create(store, href),
    };
}
